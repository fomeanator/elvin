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
        public static void Android()
        {
            // Флаг экспорта — состояние редактора, он переживает запуски.
            // Сборка APK обязана явно его снимать, иначе после AndroidLibrary
            // конвейер вечно выдаёт Gradle-проекты вместо пакета.
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            Build(BuildTarget.Android, "game.apk");
        }

        public static void Ios() => Build(BuildTarget.iOS, "ios-xcode"); // an Xcode project folder

        /// <summary>
        /// Unity as a Library: вместо самостоятельного APK — Gradle-проект, из
        /// которого хост-приложение (React Native и т.п.) забирает модуль
        /// unityLibrary. Наш экран становится компонентом чужого приложения.
        /// </summary>
        public static void AndroidLibrary()
        {
            // Экспорт проекта вместо сборки пакета — всё остальное (сцена,
            // графическое API, штамп версии) идёт тем же путём, что и APK:
            // библиотека не должна отличаться от игры ничем, кроме упаковки.
            EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
            Build(BuildTarget.Android, "android-library");
        }

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

            // Иконка — часть «это готовый продукт», а не отдельный шаг, о
            // котором надо помнить: если проект принёс свои картинки, ставим их
            // прямо здесь. Без них сборка идёт как раньше, с кубиком Unity.
            if (!AppIcon.ApplyIfPresent())
                Debug.Log("[lvn-build] иконки нет (Assets/Icon/app-icon.png) — сборка с иконкой Unity");

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

            AssertPlayable();

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
        /// <summary>
        /// ИГРА, КОТОРУЮ НЕЧЕМ ИГРАТЬ, НЕ СОБИРАЕТСЯ.
        ///
        /// <para>Офлайн-сборка несёт контент внутри
        /// (<c>Assets/StreamingAssets/lvn</c>): манифест, скрипты, арт. Если
        /// каталог есть, а манифеста в нём нет, приложение установится,
        /// откроется и покажет пустоту — узнаётся это по готовому APK, то есть
        /// через минуты сборки и установку на телефон.</para>
        ///
        /// <para>Проверка узкая намеренно. Каталога нет вовсе — это ЗАКОННАЯ
        /// онлайн-сборка (контент приедет с сервера), и падать на ней нельзя;
        /// сказать вслух — можно и нужно, чтобы «а почему пусто» не искали в
        /// телефоне. Красным становится только противоречие: контент объявлен и
        /// при этом отсутствует.</para>
        /// </summary>
        private static void AssertPlayable()
        {
            const string bundle = "Assets/StreamingAssets/lvn";
            if (!Directory.Exists(bundle))
            {
                Debug.Log("[lvn-build] офлайн-контента нет — сборка online: игра пойдёт за контентом на сервер");
                return;
            }
            var manifest = Path.Combine(bundle, "manifest.json");
            if (!File.Exists(manifest))
                throw new Exception("[lvn-build] " + bundle + " есть, а manifest.json в нём нет — " +
                                    "собранная игра откроется пустой. Экспорт положил каталог не до конца " +
                                    "или его почистили: проверьте /v1/export и содержимое StreamingAssets.");
            var scripts = Directory.Exists(Path.Combine(bundle, "content", "scripts"))
                ? Directory.GetFiles(Path.Combine(bundle, "content", "scripts"), "*.lvn").Length : 0;
            Debug.Log($"[lvn-build] офлайн-контент на месте: манифест + {scripts} глав(ы)");
            if (scripts == 0)
                Debug.LogWarning("[lvn-build] в офлайн-контенте НЕТ НИ ОДНОЙ главы (.lvn) — " +
                                 "игра запустится, но играть будет нечего");
        }

        // project has it; otherwise create an empty one.
        private static string EnsureBootScene()
        {
            const string path = "Assets/Scenes/Boot.unity";
            // Never pick an arbitrary imported demo scene. AssetDatabase order
            // changed when Blacksmith was installed, so the old fallback chose
            // ThunderHammer.unity as the player scene and silently packed the
            // entire 3D kit into a supposedly server-only APK.
            if (File.Exists(path)) return path;

            var existing = EditorBuildSettings.scenes;
            foreach (var s in existing)
                if (s.enabled && File.Exists(s.path))
                    return s.path;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }
    }
}
