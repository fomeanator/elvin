#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Lvn.Content;
using Lvn.UI.Screens;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UiLab
{
    /// <summary>
    /// РАНТАЙМ-ДРАЙВЕР СНИМКОВ ГЛАВНОЙ. Поднимается в Play, если в корне
    /// <c>ui-lab/</c> лежит флаг <c>.shots</c>
    /// («приставка|ширина|высота|id новеллы|номер главы»): проходит экран
    /// входа, ждёт хаб, при нужде отмечает прогресс по новелле (карточка
    /// «Продолжить» с главой N), ждёт арт и снимает кадр в
    /// <c>readme-shots/stage/</c> и копию на рабочий стол.
    /// </summary>
    public sealed class StageShotsDriver : MonoBehaviour
    {
        private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string FlagPath => Path.Combine(Root, ".shots");
        private static string DonePath => Path.Combine(Root, ".shots-done");
        private static string OutDir => Path.GetFullPath(Path.Combine(Root, "../readme-shots/stage"));

        private string _tag = "stage";
        private string _titleId;
        private int _chapter;
        private int _shot;
        private NovelApp _app;
        private BrowseHub _hub;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!File.Exists(FlagPath)) return;
            var go = new GameObject("StageShotsDriver");
            DontDestroyOnLoad(go);
            var d = go.AddComponent<StageShotsDriver>();
            var parts = File.ReadAllText(FlagPath).Trim().Split('|');
            if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0])) d._tag = parts[0].Trim();
            if (parts.Length > 3) d._titleId = parts[3].Trim();
            if (parts.Length > 4) int.TryParse(parts[4].Trim(), out d._chapter);
            File.Delete(FlagPath);
            if (File.Exists(DonePath)) File.Delete(DonePath);
            ContentLoader.Ktx2Only = false;
            // Вырез телефона называем сами (шестое и седьмое поле флага —
            // чёлка и домашняя полоса в пикселях): Game View при увеличенном
            // рендере врёт про безопасную зону, и шапка уезжала в середину.
            int top = 0, bottom = 0;
            if (parts.Length > 5) int.TryParse(parts[5].Trim(), out top);
            if (parts.Length > 6) int.TryParse(parts[6].Trim(), out bottom);
            Lvn.UI.LvnEdges.Simulated = new Rect(0, bottom, Screen.width, Screen.height - top - bottom);
            Debug.Log($"[shots] драйвер поднят: «{d._tag}», новелла={d._titleId ?? "-"}, глава={d._chapter}; "
                    + $"экран {Screen.width}×{Screen.height}, safeArea устройства={Screen.safeArea}, подставлен вырез {top}/{bottom}");
        }

        private void Start() => StartCoroutine(Roll());

        private IEnumerator Shoot(string name)
        {
            yield return new WaitForEndOfFrame();
            Directory.CreateDirectory(OutDir);
            var p = Path.Combine(OutDir, $"{_tag}-{++_shot:00}-{name}.png");
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var png = tex.EncodeToPNG();
            File.WriteAllBytes(p, png);
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var copy = Path.Combine(desktop, $"timeromance-{_tag}-{name}-{tex.width}x{tex.height}.png");
                File.WriteAllBytes(copy, png);
                Debug.Log($"[shots] копия: {copy}");
            }
            catch (Exception e) { Debug.LogWarning("[shots] копия на рабочий стол не удалась: " + e.Message); }
            Destroy(tex);
            Debug.Log($"[shots] {p} ({tex.width}×{tex.height})");
        }

        private static bool Covered(VisualElement e)
            => e.resolvedStyle.backgroundImage.sprite != null || e.resolvedStyle.backgroundImage.texture != null;

        // Ждём, пока каждая картинка облика (имя «stage-img») получит спрайт.
        private IEnumerator WaitArt(float seconds)
        {
            for (float t = 0f; t < seconds; t += 0.25f)
            {
                var imgs = _hub.Query(name: "stage-img").ToList();
                int missing = imgs.Count(e => !Covered(e));
                if (imgs.Count > 0 && missing == 0) { Debug.Log($"[shots] арт облика на месте: {imgs.Count}"); yield break; }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            Debug.LogWarning("[shots] арт облика не дождался целиком, снимаю как есть");
        }

        // Героиня витрины: после долгого ожидания в фоне сторож композита
        // снимал «залипший» переход и кукла пропадала. Спрашиваем сцену, стоит
        // ли она целой, и при нужде ставим заново — как это делает меню.
        private IEnumerator WaitHeroine(float seconds)
        {
            var bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var stage = _app.Stage;
            if (stage == null) yield break;
            bool Ok() => stage.Prima.Exists && stage.Prima.InFrame && stage.Prima.Whole;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                for (float t = 0f; t < seconds; t += 0.5f)
                {
                    if (Ok()) { Debug.Log($"[shots] героиня на месте ({stage.Prima.Id})"); yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                Debug.LogWarning($"[shots] героини нет (в кадре={stage.Prima.InFrame}, цела={stage.Prima.Whole}) — ставлю заново");
                var place = _app.GetType().GetMethod("PlaceMenuHeroine", bf);
                place?.Invoke(_app, new object[] { null, Lvn.LvnSender.Menu });
                yield return new WaitForSecondsRealtime(3f);
            }
        }

        // Кружок загрузок стоит поверх шапки, пока греется библиотека; на
        // снимке главной ему делать нечего — ждём, пока он спрячется.
        private IEnumerator WaitDownloads(float seconds)
        {
            var root = _hub.panel?.visualTree;
            for (float t = 0f; t < seconds; t += 0.5f)
            {
                var hud = root?.Query<DownloadHud>().First();
                if (hud == null || hud.resolvedStyle.display == DisplayStyle.None || hud.resolvedStyle.opacity < 0.05f)
                { Debug.Log("[shots] кружок загрузок спрятан"); yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Debug.LogWarning("[shots] кружок загрузок не спрятался, снимаю с ним");
        }

        // Что стоит в дереве на момент снимка: сколько шапок и хабов (двойная
        // сборка видна сразу), где и чем закрашены картинки облика.
        private void Diagnose()
        {
            var root = _hub.panel?.visualTree;
            if (root == null) return;
            // Что лежит под точкой у правого края под шапкой (там на снимках
            // виден чужой значок «≡»).
            var probe = new Vector2(1043f, 332f);
            var hit = root.panel.Pick(probe);
            for (var e = hit; e != null; e = e.parent)
                Debug.Log($"[shots]  под точкой {probe}: «{e.name}» {e.GetType().Name} bound={e.worldBound}");
            int bars = root.Query<LvnTopBar>().ToList().Count;
            int hubs = root.Query<BrowseHub>().ToList().Count;
            Debug.Log($"[shots] дерево: шапок={bars}, хабов={hubs}, экран {Screen.width}×{Screen.height}, корень панели={root.worldBound}, "
                    + $"кадров={Time.frameCount} за {Time.realtimeSinceStartup:0.0}с (≈{Time.frameCount / Mathf.Max(0.1f, Time.realtimeSinceStartup):0.0} к/с)");
            foreach (var doc in root.Children())
                Debug.Log($"[shots]  документ «{doc.name}» {doc.GetType().Name} bound={doc.worldBound} kids={doc.childCount} "
                        + $"top={doc.resolvedStyle.top:0} pos={doc.resolvedStyle.position} tr={doc.resolvedStyle.translate}");
            // Цепочка предков хаба: кто из них не во весь экран.
            for (var p = _hub.parent; p != null; p = p.parent)
                Debug.Log($"[shots]  предок «{p.name}» {p.GetType().Name} bound={p.worldBound} top={p.resolvedStyle.top:0} pos={p.resolvedStyle.position} tr={p.resolvedStyle.translate} pad={p.resolvedStyle.paddingTop:0}");
            foreach (var e in root.Query(name: "stage-img").ToList())
            {
                var bg = e.resolvedStyle.backgroundImage;
                var tex = bg.sprite != null ? bg.sprite.texture : bg.texture;
                Debug.Log($"[shots]  img {e.parent?.name}/{e.parent?.GetType().Name} bound={e.worldBound} "
                        + $"tex={(tex != null ? tex.width + "x" + tex.height + " " + tex.format : "нет")} "
                        + $"bgColor={e.resolvedStyle.backgroundColor} tint={e.resolvedStyle.unityBackgroundImageTintColor}");
            }
            // Кукла на сцене (uGUI): что у неё с видимостью на самом деле.
            var doll = GameObject.Find("vn-obj-hill");
            if (doll == null) Debug.LogWarning("[shots] объекта куклы vn-obj-hill нет в сцене");
            else
            {
                var rt = doll.GetComponent<RectTransform>();
                var corners = new Vector3[4]; if (rt != null) rt.GetWorldCorners(corners);
                var groups = doll.GetComponentsInChildren<CanvasGroup>(true);
                var images = doll.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                var canvas = doll.GetComponentInParent<Canvas>();
                var crt = canvas != null ? canvas.GetComponent<RectTransform>() : null;
                Debug.Log($"[shots] кукла: anchored={rt?.anchoredPosition} size={rt?.rect.size} scale={rt?.localScale} "
                        + $"canvas={(crt != null ? crt.rect.size.ToString() : "-")} pivot={rt?.pivot} anchorMin={rt?.anchorMin}");
                Debug.Log($"[shots] кукла: active={doll.activeInHierarchy} rect=({corners[0].x:0.00},{corners[0].y:0.00})-({corners[2].x:0.00},{corners[2].y:0.00}) "
                        + $"groups=[{string.Join(",", groups.Select(g => g.alpha.ToString("0.00")))}] "
                        + $"images={images.Length} enabled={images.Count(i => i.enabled)} withSprite={images.Count(i => i.sprite != null)} "
                        + $"alpha=[{string.Join(",", images.Take(6).Select(i => i.color.a.ToString("0.00")))}]");
                for (var t = doll.transform; t != null; t = t.parent)
                {
                    var cg = t.GetComponent<CanvasGroup>();
                    if (cg != null || !t.gameObject.activeSelf)
                        Debug.Log($"[shots]  предок куклы «{t.name}» active={t.gameObject.activeSelf} cgAlpha={(cg != null ? cg.alpha.ToString("0.00") : "-")}");
                }
            }
            var bar = root.Query<LvnTopBar>().First();
            if (bar != null)
            {
                var row = bar.Q(className: null, name: null);
                Debug.Log($"[shots] шапка: {bar.DebugState} root={bar.worldBound} insets={Lvn.UI.LvnEdges.Insets(bar)} "
                        + $"kids={string.Join(",", bar.Children().Select(k => k.GetType().Name + "@" + k.worldBound.y.ToString("0") + "/mt" + k.resolvedStyle.marginTop.ToString("0") + "/top" + k.resolvedStyle.top.ToString("0")))}");
            }
            // Подписи хаба: текст, шрифт и место — дубли и чужая гарнитура видны сразу.
            foreach (var l in _hub.Query<Label>().ToList())
            {
                if (string.IsNullOrEmpty(l.text) || l.resolvedStyle.display == DisplayStyle.None) continue;
                var fd = l.resolvedStyle.unityFontDefinition;
                string font = fd.fontAsset != null ? fd.fontAsset.name : fd.font != null ? fd.font.name : "-";
                Debug.Log($"[shots]  label «{l.text}» font={font} size={l.resolvedStyle.fontSize:0} bound=({l.worldBound.x:0},{l.worldBound.y:0} {l.worldBound.width:0}x{l.worldBound.height:0}) parent={l.parent?.name}");
            }
        }

        private void Done()
        {
            ContentLoader.Ktx2Only = true;
            File.WriteAllText(DonePath, "done");
            Debug.Log("[shots] готово");
        }

        private IEnumerator Roll()
        {
            var bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Debug.Log("[shots] жду манифест");
            LvnManifest manifest = null;
            for (var i = 0; i < 240 && manifest == null; i++)
            {
                if (_app == null) _app = FindAnyObjectByType<NovelApp>();
                if (_app != null)
                {
                    _app.AskName = false;
                    manifest = _app.GetType().GetField("_manifest", bf)?.GetValue(_app) as LvnManifest;
                }
                if (i > 0 && i % 20 == 0) Debug.Log($"[shots] жду манифест… {i / 2} с, app={(_app != null ? "есть" : "нет")}");
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (manifest == null) { Debug.LogError("[shots] манифест не приехал"); Done(); yield break; }
            Debug.Log("[shots] манифест приехал, жду экран входа или витрину");

            for (var i = 0; i < 60; i++)
            {
                var auth = _app.Shell?.Auth;
                if (auth != null && auth.panel != null && auth.resolvedStyle.display != DisplayStyle.None)
                {
                    auth.GetType().GetMethod("Confirm", bf)?.Invoke(auth, null);
                    Debug.Log("[shots] экран входа пройден");
                    break;
                }
                var h = _app.Shell?.Hub;
                if (h != null && h.panel != null && h.resolvedStyle.display != DisplayStyle.None) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }

            _hub = null;
            for (var i = 0; i < 120; i++)
            {
                if (_app == null) _app = FindAnyObjectByType<NovelApp>();
                var h = _app?.Shell?.Hub;
                if (h != null && h.panel != null)
                {
                    var hv = h.GetType().GetField("_hubView", bf)?.GetValue(h) as VisualElement;
                    var wb = hv?.worldBound ?? default;
                    bool onScreen = hv != null && wb.width > 1f && wb.x > -1f && wb.x < 1f;
                    if (onScreen) { _hub = h; break; }
                }
                if (i > 0 && i % 20 == 0) Debug.Log($"[shots] жду хаб на экране… {i / 4} с");
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (_hub == null) { Debug.LogError("[shots] витрина так и не въехала"); Done(); yield break; }
            Debug.Log("[shots] хаб на месте");

            LvnTitle marked = null;
            if (!string.IsNullOrEmpty(_titleId) && _chapter > 0)
            {
                // Манифест перечитываем: тот, что был на старте, мог быть
                // кэшем, а свежий приехал следом и заменил его в приложении.
                manifest = (_app.GetType().GetField("_manifest", bf)?.GetValue(_app) as LvnManifest) ?? manifest;
                marked = manifest.titles?.FirstOrDefault(t => t.id == _titleId);
                var ch = marked?.ChapterByNumber(_chapter);
                if (marked != null && ch != null)
                {
                    LvnProgress.StartChapter(marked, ch);
                    _hub.SetContent(manifest);
                    Debug.Log($"[shots] прогресс: {marked.id} → глава {_chapter}");
                }
                else Debug.LogWarning($"[shots] новелла/глава не найдены: {_titleId}/{_chapter}");
            }

            yield return new WaitForSecondsRealtime(4f);
            yield return WaitArt(20f);
            yield return WaitDownloads(60f);
            yield return WaitHeroine(12f);
            yield return new WaitForSecondsRealtime(1.5f);
            Diagnose();
            yield return Shoot("main");
            if (marked != null) LvnProgress.ClearCurrent(marked);
            Done();
        }
    }
}
#endif
