using System.Collections.Generic;
using MindPuck.Core;
using UnityEngine;

namespace MindPuck.Game
{
    /// <summary>
    /// Everything you see on the table, built from code: the surface, the rails, the walls of the
    /// current level, the goal pad, the puck and the gaze reticle.
    ///
    /// The core works in table units: 16 by 9, origin at the top-left, y down. In the scene the table
    /// lies on the ground plane, centred on the origin: table x runs along world +X and table y along
    /// world -Z, so "down the screen" is toward the camera.
    /// </summary>
    public sealed class TableView : MonoBehaviour
    {
        private const float WallHeight = 0.4f;
        private const float RailWidth = 0.45f;
        private const float PuckHeight = 0.2f;

        private static readonly Color SurfaceColor = new Color(0.07f, 0.11f, 0.2f);
        private static readonly Color RailColor = new Color(0.2f, 0.25f, 0.36f);
        private static readonly Color WallColor = new Color(0.36f, 0.45f, 0.62f);
        private static readonly Color PuckColor = new Color(1f, 0.74f, 0.25f);
        private static readonly Color GoalColor = new Color(0.3f, 0.95f, 0.6f);
        private static readonly Color ReticleColor = new Color(0.5f, 0.83f, 1f);

        private readonly List<Mesh> levelMeshes = new List<Mesh>();
        private readonly List<Object> owned = new List<Object>();
        private Transform levelRoot;
        private Transform puck;
        private Transform goal;
        private Transform reticle;
        private Material wallMaterial;
        private Material padMaterial;
        private Material reticleMaterial;
        private Transform reticleDot;
        private readonly Transform[] reticleRings = new Transform[2];
        private readonly Material[] ringMaterials = new Material[2];

        /// <summary>The colour that stands for a control scheme: pink magnetic, green steer, blue repulsor.</summary>
        public static Color ModeColor(GazeMode mode)
        {
            return mode == GazeMode.Attract ? new Color(1f, 0.604f, 0.835f)
                : mode == GazeMode.Steer ? new Color(0.373f, 0.89f, 0.631f)
                : new Color(0.498f, 0.831f, 1f);
        }

        public static Vector3 ToWorld(Vec p, float height = 0f)
        {
            return new Vector3((float)(p.X - Level.ArenaW / 2), height, (float)(Level.ArenaH / 2 - p.Y));
        }

        public static Vec ToTable(Vector3 world)
        {
            return new Vec(world.x + Level.ArenaW / 2, Level.ArenaH / 2 - world.z);
        }

        /// <summary>A table polygon as an outline on the ground plane, as (x, z) pairs.</summary>
        private static Vector2[] Outline(Vec[] polygon)
        {
            var outline = new Vector2[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector3 w = ToWorld(polygon[i]);
                outline[i] = new Vector2(w.x, w.z);
            }
            return outline;
        }

        /// <summary>Create the parts that do not change from level to level.</summary>
        public void Build()
        {
            double w = Level.ArenaW;
            double h = Level.ArenaH;
            double rail = RailWidth;

            Part("Surface", transform, MeshBuilder.Flat(Outline(Geometry.RectPolygon(w / 2, h / 2, w / 2, h / 2)), 0f), GameMaterials.Lit(SurfaceColor, 0.25f), true);
            Material railMaterial = GameMaterials.Lit(RailColor);
            Vec[][] rails =
            {
                Geometry.RectPolygon(w / 2, -rail / 2, w / 2 + rail, rail / 2),
                Geometry.RectPolygon(w / 2, h + rail / 2, w / 2 + rail, rail / 2),
                Geometry.RectPolygon(-rail / 2, h / 2, rail / 2, h / 2),
                Geometry.RectPolygon(w + rail / 2, h / 2, rail / 2, h / 2),
            };
            foreach (Vec[] r in rails) Part("Rail", transform, MeshBuilder.Extrude(Outline(r), 0f, WallHeight), railMaterial, true);

            wallMaterial = GameMaterials.Lit(WallColor);
            owned.Add(wallMaterial);
            levelRoot = new GameObject("Level").transform;
            levelRoot.SetParent(transform, false);

            // The goal pad: a soft disc with a brighter rim. Cup levels carry their walls as obstacles.
            goal = new GameObject("Goal").transform;
            goal.SetParent(transform, false);
            padMaterial = GameMaterials.Glow(new Color(GoalColor.r, GoalColor.g, GoalColor.b, 0.22f), 0);
            float goalRadius = (float)Level.GoalR;
            Part("Pad", goal, MeshBuilder.Flat(MeshBuilder.Circle(goalRadius, 64), 0.004f), padMaterial, false);
            Part("Rim", goal, MeshBuilder.Ring(goalRadius - 0.07f, goalRadius, 64, 0.006f), GameMaterials.Glow(new Color(GoalColor.r, GoalColor.g, GoalColor.b, 0.9f), 1), false);

            puck = new GameObject("Puck").transform;
            puck.SetParent(transform, false);
            float puckRadius = (float)Level.PuckR;
            Part("Body", puck, MeshBuilder.Extrude(MeshBuilder.Circle(puckRadius, 48), 0f, PuckHeight), GameMaterials.Lit(PuckColor, 0.7f), true);
            Part("Top", puck, MeshBuilder.Ring(puckRadius * 0.45f, puckRadius * 0.62f, 48, PuckHeight + 0.002f), GameMaterials.Glow(new Color(0.35f, 0.2f, 0.02f, 0.8f), 2), false);

            // The reticle marks the gaze point. Its rings travel outward when the gaze pushes (repulsor)
            // and inward when it pulls (magnetic).
            reticle = new GameObject("Reticle").transform;
            reticle.SetParent(transform, false);
            reticleMaterial = GameMaterials.Glow(ReticleColor, 12);
            Mesh ringMesh = MeshBuilder.Ring(0.44f, 0.5f, 48, 0.012f);
            for (int i = 0; i < reticleRings.Length; i++)
            {
                ringMaterials[i] = GameMaterials.Glow(ReticleColor, 10 + i);
                reticleRings[i] = Part("Ring", reticle, ringMesh, ringMaterials[i], false).transform;
            }
            reticleDot = Part("Dot", reticle, MeshBuilder.Flat(MeshBuilder.Circle(0.1f, 24), 0.012f), reticleMaterial, false).transform;
            reticle.gameObject.SetActive(false);
        }

        private GameObject Part(string name, Transform parent, Mesh mesh, Material material, bool shadows)
        {
            var part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = shadows;
            if (parent != levelRoot)
            {
                owned.Add(mesh);
                if (!owned.Contains(material)) owned.Add(material);
            }
            return part;
        }

        /// <summary>Replace the walls with those of a level and move the goal pad.</summary>
        public void ShowLevel(Level level)
        {
            for (int i = levelRoot.childCount - 1; i >= 0; i--) Destroy(levelRoot.GetChild(i).gameObject);
            foreach (Mesh mesh in levelMeshes) Destroy(mesh);
            levelMeshes.Clear();

            foreach (Vec[] polygon in level.Obstacles)
            {
                Mesh mesh = MeshBuilder.Extrude(Outline(polygon), 0f, WallHeight);
                levelMeshes.Add(mesh);
                Part("Obstacle", levelRoot, mesh, wallMaterial, true);
            }
            goal.localPosition = ToWorld(level.Goal.Pos);
            SetPuck(level.Start);
            SetGoalProgress(0f);
        }

        public void SetPuck(Vec position)
        {
            puck.localPosition = ToWorld(position);
        }

        /// <summary>How far the puck is through its rest on the pad, 0..1. The pad brightens as it fills.</summary>
        public void SetGoalProgress(float progress)
        {
            padMaterial.color = new Color(GoalColor.r, GoalColor.g, GoalColor.b, Mathf.Lerp(0.22f, 0.7f, Mathf.Clamp01(progress)));
        }

        /// <summary>
        /// Show the reticle at a table point, or hide it with null. A faint one means the gaze is not
        /// acting on the puck. Repulsor: blue rings moving outward. Magnetic: pink rings moving inward
        /// and a smaller dot. Steer: green and small, because the gaze stays on the puck.
        /// </summary>
        public void SetReticle(Vec? point, bool acting, GazeMode mode)
        {
            reticle.gameObject.SetActive(point.HasValue);
            if (!point.HasValue) return;
            reticle.localPosition = ToWorld(point.Value);
            bool pull = mode != GazeMode.Repel;
            Color colour = ModeColor(mode);
            float size = mode == GazeMode.Steer ? 0.55f : 1f;
            float strength = acting ? 1f : 0.35f;
            reticleMaterial.color = new Color(colour.r, colour.g, colour.b, 0.95f * strength);
            float dot = pull ? 0.6f : 1f;
            reticleDot.localScale = new Vector3(dot, 1f, dot);
            for (int i = 0; i < reticleRings.Length; i++)
            {
                // Each ring runs from the centre to the rim (or back), fading as it goes out.
                float phase = Mathf.Repeat(Time.unscaledTime * 0.9f + (float)i / reticleRings.Length, 1f);
                float travel = pull ? 1f - phase : phase;
                float scale = Mathf.Lerp(0.35f, 1.6f, travel) * size;
                reticleRings[i].localScale = new Vector3(scale, 1f, scale);
                ringMaterials[i].color = new Color(colour.r, colour.g, colour.b, Mathf.Lerp(0.95f, 0.1f, travel) * strength);
            }
        }

        private void OnDestroy()
        {
            foreach (Mesh mesh in levelMeshes) Destroy(mesh);
            foreach (Object o in owned) Destroy(o);
        }
    }
}
