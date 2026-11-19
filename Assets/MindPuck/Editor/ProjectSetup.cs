using System.IO;
using MindPuck.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MindPuck.Editor
{
    /// <summary>
    /// Generates the few assets the project needs, so none are authored by hand: the single scene, the
    /// two template materials that keep the shaders in player builds, and the build settings.
    ///
    /// From a shell:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod MindPuck.Editor.ProjectSetup.CreateScene -logFile setup.log
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/MindPuck/Scenes/MindPuck.unity";
        private const string ResourcesFolder = "Assets/MindPuck/Resources";

        [MenuItem("MindPuck/Set Up Project")]
        public static void CreateScene()
        {
            Directory.CreateDirectory(ResourcesFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            AssetDatabase.Refresh();

            EnsureMaterial(GameMaterials.LitTemplate, GameMaterials.LitShader);
            EnsureMaterial(GameMaterials.GlowTemplate, GameMaterials.GlowShader);

            // The scene holds one object. Everything else is built when it wakes.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("MindPuck").AddComponent<MindPuckGame>();
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new IOException("MindPuck: could not save " + ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            PlayerSettings.companyName = "MindAttic";
            PlayerSettings.productName = "MindPuck";
            PlayerSettings.runInBackground = true;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("MindPuck: project set up. Scene saved to " + ScenePath);
        }

        private static void EnsureMaterial(string name, string shaderName)
        {
            string path = ResourcesFolder + "/" + name + ".mat";
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new IOException("MindPuck: shader not found: " + shaderName);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                AssetDatabase.CreateAsset(new Material(shader), path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }
        }
    }
}
