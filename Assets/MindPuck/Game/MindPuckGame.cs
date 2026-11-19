using System;
using System.Threading.Tasks;
using MindAttic.EyeTracker;
using MindPuck.Core;
using UnityEngine;

namespace MindPuck.Game
{
    /// <summary>
    /// The whole game on one object. It builds the camera, the light and the table from code, runs the
    /// fixed-step simulation from the core, and draws the HUD. The rules live in MindPuck.Core; nothing
    /// here changes them.
    /// </summary>
    public sealed class MindPuckGame : MonoBehaviour
    {
        /// <summary>
        /// The puck is held still for this long at the start of every attempt, so the player can find
        /// it before their gaze starts pushing it. HOLD_MS in the web version (src/main.ts).
        /// </summary>
        public const float HoldSeconds = 0.9f;
        private const float GoalSeconds = 1.6f;
        private const string TrackerPage = "http://localhost:8787/tracker/";

        [Tooltip("The EyeTracker bridge.")]
        public string bridgeUrl = "ws://localhost:8787/gaze";
        public string room = "default";
        [Tooltip("The run. The same seed always gives the same levels.")]
        public uint seed = 1;
        [Tooltip("The level to start on, counted from zero.")]
        public int startLevel;

        private enum State
        {
            Loading,
            Playing,
            Won,
        }

        private Camera cam;
        private float fittedAspect;
        private TableView table;
        private GazeTracker tracker;
        private GazeCalibrator calibrator;

        private State state = State.Loading;
        private int levelIndex;
        private int loadingIndex;
        private Task<Level> pending;
        private string loadError;
        private Level level;
        private PuckSim sim;
        private SolverBot bot;
        private bool botMode;
        private double botAccumulator;
        private float holdLeft;
        private float goalLeft;
        private string calibrationNote;
        private float calibrationNoteUntil;

        private GUIStyle hudStyle;
        private GUIStyle bigStyle;
        private GUIStyle hintStyle;
        private Texture2D dotTexture;
        private Vector2 reticleScreen;
        private bool reticleVisible;

        private void Awake()
        {
            BuildCamera();
            BuildLight();

            table = new GameObject("Table").AddComponent<TableView>();
            table.transform.SetParent(transform, false);
            table.Build();

            // Created inactive so the address is set before the tracker connects in OnEnable.
            var gaze = new GameObject("Gaze");
            gaze.SetActive(false);
            gaze.transform.SetParent(transform, false);
            tracker = gaze.AddComponent<GazeTracker>();
            tracker.url = bridgeUrl;
            tracker.room = room;
            tracker.displayName = "MindPuck";
            tracker.useMouseFallback = true;
            calibrator = gaze.AddComponent<GazeCalibrator>();
            calibrator.tracker = tracker;
            calibrator.Completed += OnCalibrated;
            gaze.SetActive(true);

            LoadLevel(startLevel);
        }

        private void OnDestroy()
        {
            if (calibrator != null) calibrator.Completed -= OnCalibrated;
            if (dotTexture != null) Destroy(dotTexture);
        }

        // ---- scene ----

        private void BuildCamera()
        {
            var go = new GameObject("Camera");
            go.tag = "MainCamera";
            go.transform.SetParent(transform, false);
            cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.027f, 0.05f);
            cam.fieldOfView = 34f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 200f;
            go.AddComponent<AudioListener>();
            FitCamera();
        }

        /// <summary>
        /// Look down at the table with a slight tilt, from just far enough that the whole table and its
        /// rails are on screen at the current aspect ratio.
        /// </summary>
        private void FitCamera()
        {
            fittedAspect = cam.aspect;
            const float tilt = 9f;
            Vector3 back = Quaternion.Euler(-tilt, 0f, 0f) * Vector3.up;
            float halfW = (float)Level.ArenaW / 2 + 0.75f;
            float halfH = (float)Level.ArenaH / 2 + 0.75f;
            Vector3[] corners =
            {
                new Vector3(-halfW, 0f, -halfH),
                new Vector3(halfW, 0f, -halfH),
                new Vector3(-halfW, 0f, halfH),
                new Vector3(halfW, 0f, halfH),
            };
            for (float distance = 6f; distance <= 120f; distance += 0.25f)
            {
                cam.transform.position = back * distance;
                cam.transform.rotation = Quaternion.LookRotation(-back, Vector3.forward);
                bool fits = true;
                foreach (Vector3 corner in corners)
                {
                    Vector3 v = cam.WorldToViewportPoint(corner);
                    if (v.z <= 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) fits = false;
                }
                if (fits) break;
            }
        }

        private void BuildLight()
        {
            var go = new GameObject("Light");
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
            var sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.6f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.34f, 0.38f, 0.48f);
            RenderSettings.fog = false;
        }

        // ---- levels ----

        /// <summary>
        /// Generate a level off the main thread: the generator plays each candidate with the solver bot,
        /// which can take a moment. The core has no engine references, so it is safe on a worker.
        /// </summary>
        private void LoadLevel(int index)
        {
            if (index < 0) index = 0;
            loadingIndex = index;
            loadError = null;
            state = State.Loading;
            uint runSeed = seed;
            pending = Task.Run(() => LevelGen.GenerateLevel(runSeed, index));
        }

        private void FinishLoading()
        {
            Task<Level> task = pending;
            pending = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                loadError = task.Exception != null ? task.Exception.GetBaseException().Message : "cancelled";
                Debug.LogError("MindPuck: level " + (loadingIndex + 1) + " could not be generated: " + task.Exception);
                return;
            }
            level = task.Result;
            levelIndex = loadingIndex;
            sim = new PuckSim(level);
            table.ShowLevel(level);
            StartAttempt();
        }

        /// <summary>Put the puck back on its start and hold it there for a moment.</summary>
        private void StartAttempt()
        {
            if (level == null) return;
            sim.Reset();
            botAccumulator = 0;
            bot = null;
            if (botMode)
            {
                // The same noise seed the generator used when it proved this level, so the demo is the proof.
                BotOptions options = BotOptions.Default.WithSeed(Rng.MixSeed(level.Seed, level.Index, 1000 + level.Attempts - 1));
                bot = new SolverBot(level, sim.Params, options);
            }
            holdLeft = HoldSeconds;
            state = State.Playing;
            table.SetPuck(sim.Pos);
            table.SetGoalProgress(0f);
        }

        // ---- loop ----

        private void Update()
        {
            if (Mathf.Abs(cam.aspect - fittedAspect) > 0.001f) FitCamera();

            if (pending != null && pending.IsCompleted) FinishLoading();

            // Where the player is looking, on the table. Null when there is nothing to push with.
            Vec? gaze = null;
            Vec? shown = null;
            if (tracker.Confidence > 0f)
            {
                Vec? point = GazeOnTable(tracker.ScreenPoint);
                if (point.HasValue)
                {
                    gaze = point;
                    shown = point;
                }
            }
            else if (tracker.HasTracker)
            {
                // A blink or a lost face: no push, but keep the reticle where it was, dimmed.
                shown = GazeOnTable(tracker.ScreenPoint);
            }

            bool paused = calibrator.IsRunning;
            if (state == State.Won)
            {
                goalLeft -= Time.deltaTime;
                if (goalLeft <= 0f && pending == null) LoadLevel(levelIndex + 1);
            }
            else if (state == State.Playing && !paused)
            {
                if (holdLeft > 0f)
                {
                    // The hold: no steps and no clock, but the reticle is still drawn below.
                    holdLeft -= Time.deltaTime;
                }
                else if (bot != null)
                {
                    // The bot decides once per fixed step, so step here rather than through Advance.
                    double dt = sim.Params.Dt;
                    botAccumulator += Math.Min(Time.deltaTime, 0.1);
                    while (botAccumulator >= dt && !sim.Won)
                    {
                        botAccumulator -= dt;
                        sim.Step(bot.Update(sim));
                    }
                }
                else
                {
                    sim.Advance(Time.deltaTime, gaze);
                }

                if (sim.Won)
                {
                    state = State.Won;
                    goalLeft = GoalSeconds;
                }
            }

            if (bot != null && state != State.Loading)
            {
                shown = bot.Gaze;
                gaze = bot.Gaze;
            }

            if (sim != null)
            {
                table.SetPuck(sim.Pos);
                table.SetGoalProgress(state == State.Won ? 1f : (float)(sim.Dwell / sim.Params.Dwell));
            }
            bool pushing = gaze.HasValue && state == State.Playing && holdLeft <= 0f && !paused;
            table.SetReticle(paused ? null : shown, pushing);
            reticleVisible = shown.HasValue && !paused;
            if (reticleVisible)
            {
                Vector3 s = cam.WorldToScreenPoint(TableView.ToWorld(shown.Value));
                reticleScreen = new Vector2(s.x, Screen.height - s.y);
            }
        }

        /// <summary>
        /// Normalized gaze covers the whole screen. A ray through that screen point meets the table
        /// plane at the point the player is looking at.
        /// </summary>
        private Vec? GazeOnTable(Vector2 screenPoint)
        {
            Ray ray = cam.ScreenPointToRay(new Vector3(screenPoint.x, screenPoint.y, 0f));
            if (ray.direction.y >= -1e-5f) return null;
            float t = -ray.origin.y / ray.direction.y;
            return TableView.ToTable(ray.origin + ray.direction * t);
        }

        private void OnCalibrated(GazeCalibrationResult result)
        {
            calibrationNote = result.ok
                ? "calibrated, error about " + Mathf.RoundToInt(result.crossError * 100f) + "% of the screen"
                : "calibration failed: " + (string.IsNullOrEmpty(result.reason) ? "unknown reason" : result.reason);
            calibrationNoteUntil = Time.unscaledTime + 6f;
            // Start the attempt again: the puck has been sitting under a calibration screen.
            StartAttempt();
        }

        // ---- keys and HUD ----

        private void HandleKey(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.R:
                    StartAttempt();
                    break;
                case KeyCode.N:
                    if (pending == null) LoadLevel(levelIndex + 1);
                    break;
                case KeyCode.B:
                    botMode = !botMode;
                    StartAttempt();
                    break;
                case KeyCode.C:
                    calibrator.Begin();
                    break;
                case KeyCode.M:
                    tracker.useMouseFallback = !tracker.useMouseFallback;
                    break;
            }
        }

        private string SourceStatus()
        {
            if (bot != null) return "bot demo (B to take over)";
            if (tracker.HasTracker)
            {
                if (tracker.Confidence <= 0f) return "eye tracker: no face in view";
                return tracker.Latest.calibrated ? "eye tracker" : "eye tracker, not calibrated (press C)";
            }
            if (tracker.UsingMouse) return "mouse stand-in. For eye tracking open " + TrackerPage;
            return "waiting for tracker - open " + TrackerPage;
        }

        private void OnGUI()
        {
            if (calibrator.IsRunning) return;
            Event e = Event.current;
            // IMGUI key events work with either input backend, so the game needs no input package.
            if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                // A held key repeats; act on the first press only.
                if (e.keyCode != heldKey)
                {
                    heldKey = e.keyCode;
                    HandleKey(e.keyCode);
                }
                return;
            }
            if (e.type == EventType.KeyUp && e.keyCode == heldKey) heldKey = KeyCode.None;
            if (e.type != EventType.Repaint) return;
            EnsureStyles();

            float scale = Mathf.Max(1f, Screen.height / 900f);
            hudStyle.fontSize = Mathf.RoundToInt(20f * scale);
            hintStyle.fontSize = Mathf.RoundToInt(15f * scale);
            bigStyle.fontSize = Mathf.RoundToInt(96f * scale);
            float pad = 16f * scale;
            float line = 28f * scale;
            float width = Screen.width - pad * 2f;

            int shownLevel = (state == State.Loading ? loadingIndex : levelIndex) + 1;
            string top = "Level " + shownLevel;
            if (sim != null && state != State.Loading)
            {
                top += "    Time " + sim.Time.ToString("0.0") + " s    Par " + level.Par.ToString("0.0") + " s";
            }
            Shadowed(new Rect(pad, pad, width, line), top, hudStyle, Color.white);
            Shadowed(new Rect(pad, pad + line, width, line), SourceStatus(), hintStyle, new Color(0.75f, 0.85f, 1f));
            if (calibrationNote != null && Time.unscaledTime < calibrationNoteUntil)
            {
                Shadowed(new Rect(pad, pad + line * 2f, width, line), calibrationNote, hintStyle, new Color(0.75f, 0.85f, 1f));
            }

            string hints = "R restart    N skip    B bot demo " + (botMode ? "(on)" : "(off)") +
                "    C calibrate    M mouse stand-in " + (tracker.useMouseFallback ? "(on)" : "(off)");
            Shadowed(new Rect(pad, Screen.height - pad - line, width, line), hints, hintStyle, new Color(1f, 1f, 1f, 0.75f));

            var centre = new Rect(0f, Screen.height * 0.3f, Screen.width, 130f * scale);
            var below = new Rect(0f, centre.yMax, Screen.width, line);
            if (state == State.Loading)
            {
                string text = loadError != null ? "Level failed: " + loadError : "Building level " + shownLevel + "...";
                Shadowed(below, text, CentredHud(), Color.white);
            }
            else if (state == State.Won)
            {
                Shadowed(centre, "GOAL", bigStyle, new Color(0.3f, 0.95f, 0.6f));
                Shadowed(below, sim.Time.ToString("0.0") + " s  (par " + level.Par.ToString("0.0") + " s)", CentredHud(), Color.white);
            }
            else if (holdLeft > 0f)
            {
                Shadowed(below, "Look where you do not want the puck to go", CentredHud(), new Color(1f, 1f, 1f, 0.8f));
            }

            // A small dot at the exact gaze point, on top of everything, walls included.
            if (reticleVisible)
            {
                float size = 10f * scale;
                Color old = GUI.color;
                GUI.color = new Color(0.5f, 0.83f, 1f, 0.9f);
                GUI.DrawTexture(new Rect(reticleScreen.x - size / 2, reticleScreen.y - size / 2, size, size), dotTexture);
                GUI.color = old;
            }
        }

        private GUIStyle centredHud;
        private KeyCode heldKey = KeyCode.None;

        private GUIStyle CentredHud()
        {
            if (centredHud == null) centredHud = new GUIStyle(hudStyle) { alignment = TextAnchor.MiddleCenter };
            centredHud.fontSize = hudStyle.fontSize;
            return centredHud;
        }

        private static void Shadowed(Rect rect, string text, GUIStyle style, Color color)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f * color.a);
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, style);
            GUI.color = color;
            GUI.Label(rect, text, style);
            GUI.color = old;
        }

        private void EnsureStyles()
        {
            if (hudStyle != null) return;
            hudStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, wordWrap = false };
            hudStyle.normal.textColor = Color.white;
            hintStyle = new GUIStyle(hudStyle) { fontStyle = FontStyle.Normal };
            bigStyle = new GUIStyle(hudStyle) { alignment = TextAnchor.MiddleCenter };

            const int size = 32;
            dotTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            dotTexture.hideFlags = HideFlags.HideAndDontSave;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - size / 2f) * (x + 0.5f - size / 2f) + (y + 0.5f - size / 2f) * (y + 0.5f - size / 2f));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(size / 2f - d));
                }
            }
            dotTexture.SetPixels32(pixels);
            dotTexture.Apply();
        }
    }
}
