using UnityEngine;

namespace MindPuck.Game
{
    /// <summary>
    /// Makes sure the game exists. The committed scene already holds one MindPuck object; if a scene
    /// without one is played (a new empty scene, say), this creates it, so Play always works.
    /// </summary>
    public static class GameBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Ensure()
        {
            if (Object.FindAnyObjectByType<MindPuckGame>() != null) return;
            new GameObject("MindPuck").AddComponent<MindPuckGame>();
        }
    }
}
