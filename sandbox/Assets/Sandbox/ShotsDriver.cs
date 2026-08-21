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
            // Фотограф не человек: попап «Как тебя зовут?» ждал бы подтверждения
            // вечно и прогон снимал бы хаб вместо сцены (так и вышло однажды).
            app.AskName = false;
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
            // ГЛАВУ ОТКРЫВАЕТ ВИТРИНА, А НЕ МЫ. Прямой вызов PlayChapterAsync
            // рефлексией запускает историю, но витрину со сцены убирает
            // ОБОЛОЧКА — и она остаётся сверху весь прогон: фотограф честно
            // снимает меню, пока за ним играет глава. Именно так и вышло, и по
            // тем кадрам «переходов не видно» читалось как поломка движка.
            // Поэтому говорим витрине то же, что говорит палец игрока.
            Lvn.UI.Screens.LvnProgress.SetCurrent(title as Lvn.Content.LvnTitle,
                chapter as Lvn.Content.LvnChapter); // какую главу открыть
            var shell = app.Shell;
            var hub = shell?.Hub;
            var carousel = shell?.Carousel;
            bool asked = false;
            for (var i = 0; i < 60 && !asked; i++)
            {
                if (hub != null)
                {
                    var tcsField = hub.GetType().GetField("_tcs", bf);
                    if (tcsField?.GetValue(hub) is object tcs)
                    {
                        tcs.GetType().GetMethod("TrySetResult")?.Invoke(tcs, new object[] { title });
                        asked = true;
                        break;
                    }
                }
                if (carousel != null && carousel.style.display != UnityEngine.UIElements.DisplayStyle.None)
                {
                    carousel.RequestPlay(titles.IndexOf(title));
                    asked = true;
                    break;
                }
                yield return new WaitForSeconds(0.25f);
            }
            if (!asked)
            {
                Debug.LogWarning("[shots] витрина не спросила — открываю напрямую (витрина останется на экране)");
                app.GetType().GetMethod("PlayChapterAsync", bf)?.Invoke(app, new[] { title, chapter, (object)"Reader" });
            }
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
                // Хвост шага — серия из трёх кадров: переходы идут ~0.35с, и
                // одиночный снимок раз в 2 секунды их никогда не застаёт.
                // Серия ВПРИТЫК: продуктовый переход идёт ~0.175 s, и снимок
                // раз в десятую секунды застаёт его в лучшем случае дважды.
                for (var s = 0; s < 12; s++)
                {
                    Snap("t" + s);
                    yield return new WaitForSeconds(0.04f);
                }
            }
            Debug.Log("[shots] done");
        }
    }
}
#endif
