using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Lvn.EditorTools
{
    /// <summary>
    /// Headless player builds for exported LVN projects — the missing last mile
    /// of "author in the IDE → hand your partner an APK":
    ///
    ///   Unity -batchmode -projectPath &lt;exported&gt; \
    ///         -executeMethod Lvn.EditorTools.CliBuild.Android \
    ///         [-quit] -logFile build.log
    ///
    /// Output path comes from LVN_BUILD_OUT (default Builds/game.apk under the
    /// project). An exported project ships no scene — the engine boots itself
    /// from a [RuntimeInitializeOnLoadMethod] — but a player build needs at
    /// least one, so an empty bootstrap scene is created on the fly.
    /// </summary>
    public static class CliBuild
    {
        public static void Android() => Build(BuildTarget.Android, "game.apk");

        public static void Ios() => Build(BuildTarget.iOS, "ios-xcode"); // an Xcode project folder

        private static void Build(BuildTarget target, string defaultName)
        {
            var outPath = Environment.GetEnvironmentVariable("LVN_BUILD_OUT");
            if (string.IsNullOrEmpty(outPath))
                outPath = Path.Combine("Builds", defaultName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");

            // Stamp the build moment into the app version: it rides every
            // device-log batch (LvnLogShip's device header), so "which build is
            // this install actually running?" is answerable from the server —
            // emulators are known to silently skip reinstalls.
            // Digits-and-dots only: iOS rejects any other bundle version shape
            // (Android takes anything, so one format serves both).
            var stamp = DateTime.Now.ToString("yyyyMMdd.HHmm");
            PlayerSettings.bundleVersion = stamp;
            Debug.Log($"[lvn-build] version stamp {stamp}");

            // LVN_BUILD_DEV=1 → Development player: Debug.isDebugBuild turns on,
            // which arms the test-lane launch overrides (lvn_server intent extra /
            // LVN_SERVER env) — the QA smoke builds use this to hit a local server.
            var dev = Environment.GetEnvironmentVariable("LVN_BUILD_DEV") == "1";
            if (dev) Debug.Log("[lvn-build] development build (test overrides armed)");

            // Android defaults to Auto graphics APIs (Vulkan first, GLES3
            // fallback) — but Unity 6's Vulkan path is already known to hang
            // the Google AVD before the first log line (qa/monkey.sh pins
            // -feature -Vulkan for exactly this reason). A real device/host
            // emulator (BlueStacks etc.) has no such escape hatch: it just
            // tries Vulkan and the process dies within a second of the
            // activity displaying. Pin GLES3-only so every Android build gets
            // the path that's actually proven stable, not just the AVD tests.
            if (target == BuildTarget.Android)
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                    new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
                Debug.Log("[lvn-build] Android graphics API pinned to OpenGLES3 (Vulkan disabled)");
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { EnsureBootScene() },
                locationPathName = outPath,
                target = target,
                options = dev ? BuildOptions.Development : BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[lvn-build] {summary.result}: {summary.totalSize / (1024 * 1024)} MB → {outPath} " +
                      $"({summary.totalErrors} errors, {summary.totalWarnings} warnings)");
            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1); // make CI/scripts fail loudly
        }

        // Exported projects self-boot (the template's Boot.cs creates the
        // shell package's NovelApp before the first scene loads), so the
        // build just needs SOME scene. Reuse one if the
        // project has it; otherwise create an empty one.
        private static string EnsureBootScene()
        {
            const string path = "Assets/Scenes/Boot.unity";
            var existing = EditorBuildSettings.scenes;
            foreach (var s in existing)
                if (s.enabled && File.Exists(s.path))
                    return s.path;
            var found = AssetDatabase.FindAssets("t:SceneAsset");
            if (found.Length > 0)
                return AssetDatabase.GUIDToAssetPath(found[0]);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }
    }
}
