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
using UnityEngine.UIElements;

namespace Lvn.Sandbox
{
    public sealed class ShotsDriver : MonoBehaviour
    {
        private static string FlagPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../.shoot-readme"));
        private static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../readme-shots"));

        private int _chapter = 1;
        private string _titleId = "waylight";
        private int _shot;
        private bool _wardrobe;   // флаг «wardrobe <title> <ch>»: снять гардероб из меню

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
            if (parts.Length > 0 && parts[0] == "wardrobe")
            {
                d._wardrobe = true;
                parts = parts.Skip(1).ToArray();
            }
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
            Lvn.UI.Screens.LvnProgress.ChooseChapter(title as Lvn.Content.LvnTitle,
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
                if (carousel != null && carousel.style.display != DisplayStyle.None)
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

            if (_wardrobe) { yield return Wardrobe(); yield break; }

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
                for (var s = 0; s < 10; s++)
                {
                    Snap("t" + s);
                    yield return new WaitForSeconds(0.1f);
                }
            }
            Debug.Log("[shots] done");
        }

        // ГАРДЕРОБ ИЗ БЫСТРОГО МЕНЮ — ровно тем путём, которым идёт палец:
        // кнопка меню, потом пункт в списке. Звать OpenWardrobeFromMenuAsync
        // рефлексией бессмысленно — так проверяется код, а жалоба про экран.
        private IEnumerator Wardrobe()
        {
            // дать истории показать первую реплику
            for (var i = 0; i < 6; i++)
            {
                var st = FindAnyObjectByType<Lvn.UI.VnStage>();
                if (st?.Player != null) { st.Player.Advance(); }
                yield return new WaitForSeconds(1.2f);
            }
            Snap("story");

            var stage = FindAnyObjectByType<Lvn.UI.VnStage>();
            if (stage == null) { Debug.LogError("[shots] нет сцены"); yield break; }
            var root = stage.GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null) { Debug.LogError("[shots] нет корня документа"); yield break; }

            // Кнопка меню без подписи: гамбургер нарисован тремя полосками
            // (глиф ☰ на Android — «тофу»). Ищем её по этому признаку внутри
            // элемента vn-menu, а не по тексту, которого нет.
            var menuRoot = root.Q("vn-menu") ?? root;
            if (!ClickButtonWhere(menuRoot, b => string.IsNullOrEmpty(b.text) && b.childCount == 3))
                Debug.LogError("[shots] кнопку быстрого меню не нашёл");
            yield return new WaitForSeconds(1.5f);
            Snap("menu");
            foreach (var b2 in root.Query<Button>().ToList())
                if (!string.IsNullOrEmpty(b2.text)) Debug.Log("[shots] кнопка на экране: «" + b2.text + "»");

            // Пункт гардероба ищем по подписи из манифеста.
            var app2 = FindAnyObjectByType<Lvn.UI.Screens.NovelApp>();
            var bf2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var man = app2?.GetType().GetField("_manifest", bf2)?.GetValue(app2);
            string label = "Wardrobe";
            var ui = man?.GetType().GetField("ui")?.GetValue(man);
            var wcfg = ui?.GetType().GetField("wardrobe")?.GetValue(ui);
            var ml = wcfg?.GetType().GetField("menu_label")?.GetValue(wcfg) as string;
            if (!string.IsNullOrEmpty(ml)) label = ml;
            Debug.Log("[shots] ищу пункт меню: " + label);

            if (!ClickButtonWhere(root, b => (b.text ?? "").Trim() == label))
                Debug.LogError("[shots] пункт гардероба не найден в меню");
            for (var i = 0; i < 8; i++)
            {
                yield return new WaitForSeconds(0.8f);
                Snap("wardrobe" + i);
            }
            Debug.Log("[shots] гардероб снят");
        }

        private static bool ClickButtonWhere(VisualElement root,
                                             Func<Button, bool> match)
        {
            foreach (var b in root.Query<Button>().ToList())
            {
                if (!match(b)) continue;
                Debug.Log($"[shots] жму «{b.text}» ({b.name})");
                // ПАЛЬЦЕМ, А НЕ ClickEvent'ом: у кнопки UI Toolkit нажатие ловит
                // манипулятор Clickable, и он слушает pointer down/up. Голый
                // ClickEvent пролетает мимо — кнопка «нажата», а обработчик молчит.

                using (var down = PointerDownEvent.GetPooled())
                {
                    down.target = b;
                    b.SendEvent(down);
                }
                using (var up = PointerUpEvent.GetPooled())
                {
                    up.target = b;
                    b.SendEvent(up);
                }
                using (var click = ClickEvent.GetPooled())
                {
                    click.target = b;
                    b.SendEvent(click);   // запас: часть кнопок висит прямо на нём
                }
                return true;
            }
            return false;
        }
    }
}
#endif
