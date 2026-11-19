using UnityEngine;

namespace MindPuck.Game
{
    /// <summary>
    /// Materials made in code. Each one is a copy of a template in Assets/MindPuck/Resources, which the
    /// editor setup script generates. The templates exist so a player build includes the two shaders
    /// (Standard and Sprites/Default): a shader only reached through Shader.Find is stripped from builds.
    /// </summary>
    public static class GameMaterials
    {
        public const string LitTemplate = "MindPuckLit";
        public const string GlowTemplate = "MindPuckGlow";
        public const string LitShader = "Standard";
        public const string GlowShader = "Sprites/Default";

        /// <summary>An opaque, lit surface.</summary>
        public static Material Lit(Color color, float smoothness = 0.35f)
        {
            Material material = Create(LitTemplate, LitShader);
            material.color = color;
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            return material;
        }

        /// <summary>
        /// An unlit, see-through colour for markings drawn on the table. order sorts overlapping
        /// markings: a higher number is drawn later, on top.
        /// </summary>
        public static Material Glow(Color color, int order = 0)
        {
            Material material = Create(GlowTemplate, GlowShader);
            material.color = color;
            material.renderQueue = 3000 + order;
            return material;
        }

        private static Material Create(string template, string shaderName)
        {
            Material source = Resources.Load<Material>(template);
            if (source != null) return new Material(source);
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("MindPuck: shader \"" + shaderName + "\" is missing. Run MindPuck > Set Up Project in the editor.");
                shader = Shader.Find("Hidden/InternalErrorShader");
            }
            return new Material(shader);
        }
    }
}
