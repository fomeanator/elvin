#if UNITY_EDITOR
// Runtime README photographer. The editor-side trigger only enters playmode;
// THIS component boots with the game (no domain-reload fragility), waits for
// the browse screen, dives into the requested Waylight chapter by reflection,
// advances the story and screenshots the beats into <repo>/readme-shots/.
// Trigger: write the chapter number into sandbox/.shoot-readme and press Play.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Lvn.Sandbox
{
    public sealed class ShotsDriver : MonoBehaviour
    {
        private static string FlagPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../.shoot-readme"));
        private static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../readme-shots"));

        private int _chapter = 1;
        private string _titleId = "waylight";
        private int _shot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!File.Exists(FlagPath)) return;
            var go = new GameObject("ShotsDriver");
            DontDestroyOnLoad(go);
            var d = go.AddComponent<ShotsDriver>();
            // Flag forms: "4" (legacy, waylight ch4) or "tour 1" (title id + chapter).
            var parts = File.ReadAllText(FlagPath).Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) { d._titleId = parts[0]; int.TryParse(parts[1], out d._chapter); }
            else if (parts.Length == 1 && !int.TryParse(parts[0], out d._chapter)) d._titleId = parts[0];
            if (d._chapter < 1) d._chapter = 1;
            File.Delete(FlagPath);
            Debug.Log($"[shots] runtime driver up, title {d._titleId} chapter {d._chapter}");
        }

        private void Start() => StartCoroutine(Roll());

        private void Snap(string name)
        {
            Directory.CreateDirectory(OutDir);
            var p = Path.Combine(OutDir, $"ch{_chapter}-{++_shot:00}-{name}.png");
            ScreenCapture.CaptureScreenshot(p, 1);
            Debug.Log("[shots] " + p);
        }

        private IEnumerator Roll()
        {
            var app = FindAnyObjectByType<Lvn.UI.Screens.NovelApp>();
            if (app == null) { Debug.LogError("[shots] no NovelApp"); yield break; }
            var bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            // Wait for the manifest (the browse screen follows right after).
            object manifest = null;
            for (var i = 0; i < 120 && manifest == null; i++)
            {
                manifest = app.GetType().GetField("_manifest", bf)?.GetValue(app);
                yield return new WaitForSeconds(0.5f);
            }
            if (manifest == null) { Debug.LogError("[shots] manifest never arrived"); yield break; }
            yield return new WaitForSeconds(3f);
            Snap("browse");
            yield return new WaitForSeconds(1f);

            // Dive into the requested Waylight chapter.
            var titles = (manifest.GetType().GetField("titles")?.GetValue(manifest) as System.Collections.IEnumerable)?.Cast<object>().ToList();
            var title = titles?.FirstOrDefault(t => _titleId.Equals(t.GetType().GetField("id")?.GetValue(t) as string)) ?? titles?.FirstOrDefault();
            if (title == null) { Debug.LogError("[shots] no title"); yield break; }
            var seasons = title.GetType().GetField("seasons")?.GetValue(title) as System.Collections.IEnumerable;
            var chapters = seasons == null ? null : seasons.Cast<object>()
                .SelectMany(se => ((se.GetType().GetField("chapters")?.GetValue(se) as System.Collections.IEnumerable) ?? Array.Empty<object>()).Cast<object>())
                .ToList();
            if (chapters == null || chapters.Count == 0) { Debug.LogError("[shots] no chapters"); yield break; }
            var chapter = chapters[Mathf.Clamp(_chapter, 1, chapters.Count) - 1];
            app.GetType().GetMethod("PlayChapterAsync", bf)?.Invoke(app, new[] { title, chapter, (object)"Reader" });
            Debug.Log("[shots] chapter launched");

            yield return new WaitForSeconds(8f);
            Snap("open");

            for (var beat = 0; beat < 30; beat++)
            {
                yield return new WaitForSeconds(2.2f);
                var stage = FindAnyObjectByType<Lvn.UI.VnStage>();
                var player = stage != null ? stage.Player : null;
                if (player == null) continue;
                // An open text-input overlay pauses the story — type like a player.
                var awaiting = stage.GetType().GetField("_awaitingInput", bf)?.GetValue(stage) as bool? ?? false;
                if (awaiting)
                {
                    Snap("input");
                    yield return new WaitForSeconds(0.4f);
                    Debug.Log("[shots] confirming input");
                    stage.GetType().GetMethod("ConfirmInput", bf)?.Invoke(stage, new object[] { "Biscuit" });
                    continue;
                }
                Snap(player.AtChoice ? "choice" : "beat");
                yield return new WaitForSeconds(0.4f); // let the capture flush pre-advance
                if (player.AtChoice) player.Choose(0);
                else player.Advance();
            }
            Debug.Log("[shots] done");
        }
    }
}
#endif
