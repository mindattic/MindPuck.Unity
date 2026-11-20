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
        private const string ModeKey = "mindpuck.mode";
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
        /// <summary>Magnetic (attract) is the default. F1 steps to the next; the choice is remembered in PlayerPrefs.</summary>
        private GazeMode mode = GazeMode.Attract;

        /// <summary>The order F1 steps through (GAZE_MODES in TS).</summary>
        private static readonly GazeMode[] Modes = { GazeMode.Attract, GazeMode.Steer, GazeMode.Repel };

        private static string ModeId(GazeMode m)
        {
            return m == GazeMode.Attract ? "attract" : m == GazeMode.Steer ? "steer" : "repel";
        }

        private static string ModeName(GazeMode m)
        {
            return m == GazeMode.Attract ? "Magnetic" : m == GazeMode.Steer ? "Steer" : "Repulsor";
        }

        private static string ModeHelp(GazeMode m)
        {
            return m == GazeMode.Attract ? "Magnetic: the puck goes to where you look."
                : m == GazeMode.Steer ? "Steer: look at the edge of the puck on the side it should go. Look at its centre to let it be."
                : "Repulsor: the puck is pushed away from where you look.";
        }

        private static bool TryParseMode(string id, out GazeMode m)
        {
            foreach (GazeMode candidate in Modes)
            {
                if (ModeId(candidate) == id)
                {
                    m = candidate;
                    return true;
                }
            }
            m = GazeMode.Attract;
            return false;
        }
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
            tracker.Sample += OnSample;
            gaze.SetActive(true);

            GazeMode saved;
            if (TryParseMode(PlayerPrefs.GetString(ModeKey, "attract"), out saved)) mode = saved;
            ReadArguments();
            LoadLevel(startLevel);
        }

        // ---- diagnostics ----
        //
        // Command-line switches for checking a player build without a person at the keyboard:
        //   -mindpuck-bot                  start with the bot demo on
        //   -mindpuck-mode attract|steer|repel   play that control scheme this run (not remembered)
        //   -mindpuck-calibrate            start a calibration as soon as a tracker sends gaze
        //   -mindpuck-level <n>            start on level n (counted from one)
        //   -mindpuck-shot <file.png>      save a screenshot, then quit
        //   -mindpuck-shot-after <seconds> when to take it (default 5)
        //   -mindpuck-quit-after <seconds> quit after this long, with or without a screenshot
        // The game also logs one line when the tracker count changes, when the first gaze sample
        // arrives, when a calibration ends and on each win. Every line starts with "MindPuck:".

        private string shotPath;
        private float shotAfter = 5f;
        private float quitAfter = -1f;
        private bool shotTaken;
        private float quitAt = -1f;
        private bool calibrateAtLaunch;
        private float startedAt;
        private int lastTrackers = -1;
        private bool lastConnected;
        private bool firstSampleLogged;

        private void ReadArguments()
        {
            startedAt = Time.realtimeSinceStartup;
            string[] args = Environment.GetCommandLineArgs();
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                float seconds;
                int number;
                switch (args[i])
                {
                    case "-mindpuck-bot":
                        botMode = true;
                        break;
                    case "-mindpuck-mode":
                        GazeMode asked;
                        if (next != null && TryParseMode(next, out asked)) mode = asked;
                        break;
                    case "-mindpuck-calibrate":
                        calibrateAtLaunch = true;
                        break;
                    case "-mindpuck-level":
                        if (next != null && int.TryParse(next, System.Globalization.NumberStyles.Integer, invariant, out number)) startLevel = Mathf.Max(0, number - 1);
                        break;
                    case "-mindpuck-shot":
                        shotPath = next;
                        break;
                    case "-mindpuck-shot-after":
                        if (next != null && float.TryParse(next, System.Globalization.NumberStyles.Float, invariant, out seconds)) shotAfter = seconds;
                        break;
                    case "-mindpuck-quit-after":
                        if (next != null && float.TryParse(next, System.Globalization.NumberStyles.Float, invariant, out seconds)) quitAfter = seconds;
                        break;
                }
            }
        }

        private void Diagnose()
        {
            GazeBridgeClient client = tracker.Client;
            bool connected = client != null && client.Connected;
            int trackers = client != null ? client.Trackers : 0;
            if (trackers != lastTrackers || connected != lastConnected)
            {
                lastTrackers = trackers;
                lastConnected = connected;
                if (trackers == 0) firstSampleLogged = false;
                Debug.Log("MindPuck: bridge " + (connected ? "connected" : "not connected") + ", trackers " + trackers);
            }

            if (calibrateAtLaunch && firstSampleLogged && !calibrator.IsRunning)
            {
                calibrateAtLaunch = false;
                Debug.Log("MindPuck: calibration started");
                calibrator.Begin(true);
            }

            float elapsed = Time.realtimeSinceStartup - startedAt;
            if (shotPath != null && !shotTaken && elapsed >= shotAfter)
            {
                shotTaken = true;
                ScreenCapture.CaptureScreenshot(shotPath);
                Debug.Log("MindPuck: screenshot " + shotPath);
                // The file is written at the end of the frame; leave a moment before quitting.
                quitAt = Time.realtimeSinceStartup + 1f;
            }
            if (quitAfter >= 0f && quitAt < 0f && elapsed >= quitAfter && (shotPath == null || shotTaken)) quitAt = Time.realtimeSinceStartup;
            if (quitAt >= 0f && Time.realtimeSinceStartup >= quitAt)
            {
                quitAt = float.MaxValue;
                Debug.Log("MindPuck: quitting");
                Application.Quit();
            }
        }

        private void OnSample(GazeSample sample)
        {
            if (firstSampleLogged || tracker.UsingMouse) return;
            firstSampleLogged = true;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Debug.Log("MindPuck: first gaze sample x " + sample.x.ToString("0.000", invariant) + " y " + sample.y.ToString("0.000", invariant) +
                " confidence " + sample.confidence.ToString("0.00", invariant) + " face " + sample.face + " blink " + sample.blink +
                " calibrated " + sample.calibrated);
        }

        private string Source()
        {
            if (bot != null) return "bot";
            return tracker.UsingMouse ? "mouse" : "gaze";
        }

        private void OnDestroy()
        {
            if (calibrator != null) calibrator.Completed -= OnCalibrated;
            if (tracker != null) tracker.Sample -= OnSample;
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
            const float tilt = 5f;
            Vector3 back = Quaternion.Euler(-tilt, 0f, 0f) * Vector3.up;
            float halfW = (float)Level.ArenaW / 2 + 0.75f;
            // Extra room above and below keeps the HUD lines off the table.
            float halfH = (float)Level.ArenaH / 2 + 1.25f;
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
            sim.Mode = mode;
            botAccumulator = 0;
            bot = null;
            if (botMode)
            {
                // The same noise seed and mode the generator used when it proved this level, so the demo is the proof.
                BotOptions options = BotOptions.Default.With(Rng.MixSeed(level.Seed, level.Index, 1000 + level.Attempts - 1), mode);
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
            Diagnose();

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
                    var invariant = System.Globalization.CultureInfo.InvariantCulture;
                    Debug.Log("MindPuck: win level " + (levelIndex + 1) + " time " + sim.Time.ToString("0.00", invariant) +
                        " par " + level.ParFor(mode).ToString("0.0", invariant) + " mode " + ModeId(mode) + " source " + Source());
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
            table.SetReticle(paused ? null : shown, pushing, mode);
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
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            Debug.Log("MindPuck: calibration result ok " + result.ok + " targets " + result.targets + " samples " + result.samples +
                " fitError " + result.fitError.ToString("0.000", invariant) + " crossError " + result.crossError.ToString("0.000", invariant) +
                (string.IsNullOrEmpty(result.reason) ? "" : " reason " + result.reason));
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
                case KeyCode.F1:
                    ToggleMode();
                    break;
                case KeyCode.C:
                    calibrator.Begin();
                    break;
                case KeyCode.M:
                    tracker.useMouseFallback = !tracker.useMouseFallback;
                    break;
            }
        }

        /// <summary>Step to the next control scheme, remember it, and start the attempt again.</summary>
        private void ToggleMode()
        {
            mode = Modes[(Array.IndexOf(Modes, mode) + 1) % Modes.Length];
            PlayerPrefs.SetString(ModeKey, ModeId(mode));
            PlayerPrefs.Save();
            calibrationNote = ModeHelp(mode);
            calibrationNoteUntil = Time.unscaledTime + 4f;
            Debug.Log("MindPuck: mode " + ModeId(mode));
            StartAttempt();
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
                top += "    Time " + sim.Time.ToString("0.0") + " s    Par " + level.ParFor(mode).ToString("0.0") + " s";
            }
            Shadowed(new Rect(pad, pad, width, line), top, hudStyle, Color.white);
            Shadowed(new Rect(pad, pad + line, width, line), SourceStatus(), hintStyle, new Color(0.75f, 0.85f, 1f));
            if (calibrationNote != null && Time.unscaledTime < calibrationNoteUntil)
            {
                Shadowed(new Rect(pad, pad + line * 2f, width, line), calibrationNote, hintStyle, new Color(0.75f, 0.85f, 1f));
            }

            // The key hints sit at the top right, clear of the mode label at the bottom centre.
            string hints = "F1 mode    R restart    N skip    B bot " + (botMode ? "(on)" : "(off)") +
                "    C calibrate    M mouse " + (tracker.useMouseFallback ? "(on)" : "(off)");
            Shadowed(new Rect(pad, pad, width, line), hints, RightHint(), new Color(1f, 1f, 1f, 0.75f));
            DrawModeLabel(scale);

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
                Shadowed(below, sim.Time.ToString("0.0") + " s  (par " + level.ParFor(mode).ToString("0.0") + " s)", CentredHud(), Color.white);
            }
            else if (holdLeft > 0f)
            {
                string how = mode == GazeMode.Attract ? "Look where you want the puck to go."
                    : mode == GazeMode.Steer ? "Look at the edge of the puck on the side it should go. Look at its centre to let it be."
                    : "Look behind the puck to push it. Look far away to let it glide.";
                Shadowed(below, how, CentredHud(), new Color(1f, 1f, 1f, 0.8f));
            }

            // A small dot at the exact gaze point, on top of everything, walls included.
            if (reticleVisible)
            {
                float size = 10f * scale;
                Color old = GUI.color;
                Color dotColour = TableView.ModeColor(mode);
                GUI.color = new Color(dotColour.r, dotColour.g, dotColour.b, 0.9f);
                GUI.DrawTexture(new Rect(reticleScreen.x - size / 2, reticleScreen.y - size / 2, size, size), dotTexture);
                GUI.color = old;
            }
        }

        /// <summary>
        /// The label at the bottom centre always says which control scheme is live:
        /// "Mode 2 of 3  STEER  F1 next", the name in the colour of the mode.
        /// </summary>
        private void DrawModeLabel(float scale)
        {
            if (modeNameStyle == null)
            {
                modeNameStyle = new GUIStyle(hudStyle) { alignment = TextAnchor.MiddleCenter };
                modeSideStyle = new GUIStyle(hintStyle) { alignment = TextAnchor.MiddleCenter };
            }
            modeNameStyle.fontSize = Mathf.RoundToInt(24f * scale);
            modeSideStyle.fontSize = Mathf.RoundToInt(15f * scale);

            string left = "Mode " + (Array.IndexOf(Modes, mode) + 1) + " of " + Modes.Length;
            string name = ModeName(mode).ToUpperInvariant();
            const string right = "F1 next";
            float gap = 14f * scale;
            float leftW = modeSideStyle.CalcSize(new GUIContent(left)).x;
            float nameW = modeNameStyle.CalcSize(new GUIContent(name)).x;
            float rightW = modeSideStyle.CalcSize(new GUIContent(right)).x;
            float inner = leftW + gap + nameW + gap + rightW;
            float height = 36f * scale;
            float padX = 18f * scale;
            var box = new Rect((Screen.width - inner) / 2f - padX, Screen.height - height - 8f * scale, inner + padX * 2f, height);

            Color old = GUI.color;
            GUI.color = new Color(0.03f, 0.05f, 0.1f, 0.85f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = old;
            float x = box.x + padX;
            Shadowed(new Rect(x, box.y, leftW, height), left, modeSideStyle, new Color(1f, 1f, 1f, 0.7f));
            x += leftW + gap;
            Shadowed(new Rect(x, box.y, nameW, height), name, modeNameStyle, TableView.ModeColor(mode));
            x += nameW + gap;
            Shadowed(new Rect(x, box.y, rightW, height), right, modeSideStyle, new Color(1f, 1f, 1f, 0.7f));
        }

        private GUIStyle modeNameStyle;
        private GUIStyle modeSideStyle;
        private GUIStyle rightHint;

        private GUIStyle RightHint()
        {
            if (rightHint == null) rightHint = new GUIStyle(hintStyle) { alignment = TextAnchor.MiddleRight };
            rightHint.fontSize = hintStyle.fontSize;
            return rightHint;
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
