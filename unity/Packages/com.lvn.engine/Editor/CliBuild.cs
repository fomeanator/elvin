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

            var options = new BuildPlayerOptions
            {
                scenes = new[] { EnsureBootScene() },
                locationPathName = outPath,
                target = target,
                options = BuildOptions.None,
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
