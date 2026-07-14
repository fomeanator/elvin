// Editor-side trigger for the README photographer: sizes the Game view and
// enters playmode. The actual shooting lives in the RUNTIME ShotsDriver
// (Assets/Sandbox/ShotsDriver.cs) — editor update callbacks die on the
// domain reload that playmode entry causes, runtime components don't.
// Trigger: write a chapter number into sandbox/.shoot-readme, focus Unity
// (or Assets > Refresh); or Tools > Readme Shots for the current flag.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lvn.SandboxEditor
{
    public static class ReadmeShots
    {
        [InitializeOnLoadMethod]
        private static void AutoRun()
        {
            var flag = Path.GetFullPath(Path.Combine(Application.dataPath, "../.shoot-readme"));
            if (!File.Exists(flag) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            EditorApplication.delayCall += Run; // the runtime driver consumes the flag
        }

        [MenuItem("Tools/Readme Shots")]
        public static void Run()
        {
            try
            {
                PlayModeWindow.SetViewType(PlayModeWindow.PlayModeViewTypes.GameView);
                PlayModeWindow.SetCustomRenderingResolution(1080, 1920, "readme"); // phone-portrait product
            }
            catch (Exception e) { Debug.LogWarning("gameview size: " + e.Message); }
            EditorApplication.EnterPlaymode();
            Debug.Log("[shots] entering playmode — the runtime driver takes it from here");
        }
    }
}
