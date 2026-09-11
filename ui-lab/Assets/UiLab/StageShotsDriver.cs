#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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

        private static string LogPath => Path.Combine(Root, ".shots-log");

        private void Start()
        {
            // Свои строки — в файл рядом с флагами: терминалу нужны только они,
            // а лог редактора может оказаться недоступен.
            try { File.WriteAllText(LogPath, ""); } catch (Exception) { }
            Application.logMessageReceived += Mirror;
            StartCoroutine(Roll());
        }

        private void OnDestroy() => Application.logMessageReceived -= Mirror;

        private static void Mirror(string condition, string stack, LogType type)
        {
            if (!condition.StartsWith("[shots]") && !condition.Contains("СТОРОЖ") && type != LogType.Exception) return;
            try { File.AppendAllText(LogPath, condition + "\n"); } catch (Exception) { }
        }

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
            for (var i = 0; i < 240; i++)
            {
                if (_app == null) _app = FindAnyObjectByType<NovelApp>();
                var h = _app?.Shell?.Hub;
                if (h != null && h.panel != null)
                {
                    var hv = h.GetType().GetField("_hubView", bf)?.GetValue(h) as VisualElement;
                    if (OnScreen(hv)) { _hub = h; break; }
                }
                if (i > 0 && i % 20 == 0) Debug.Log($"[shots] жду хаб на экране… {i / 4} с");
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (_hub == null)
            {
                // Что стоит на экране вместо витрины — в лог и в кадр.
                var h = _app?.Shell?.Hub;
                var hv = h?.GetType().GetField("_hubView", bf)?.GetValue(h) as VisualElement;
                Debug.LogError($"[shots] витрина так и не въехала: hub={(h != null)} panel={(h?.panel != null)} "
                             + $"view={(hv != null ? hv.worldBound.ToString() + " display=" + hv.resolvedStyle.display + " tr=" + hv.resolvedStyle.translate : "нет")} "
                             + $"auth={(_app?.Shell?.Auth != null && _app.Shell.Auth.resolvedStyle.display != DisplayStyle.None)}");
                _hub = h;
                if (_hub != null) yield return Shoot("stuck");
                Done(); yield break;
            }
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

            // Сценарию загрузчика нужен живой кружок — идём к нему сразу,
            // пока библиотека греется, не дожидаясь тишины сети и арта.
            if (_tag.StartsWith("qa")) { yield return Qa(); if (marked != null) LvnProgress.ClearCurrent(marked); Done(); yield break; }
            if (_tag.StartsWith("dlvideo")) { yield return Video(); if (marked != null) LvnProgress.ClearCurrent(marked); Done(); yield break; }
            if (_tag.StartsWith("dl")) { yield return Downloads(); if (marked != null) LvnProgress.ClearCurrent(marked); Done(); yield break; }
            yield return new WaitForSecondsRealtime(4f);
            yield return WaitArt(20f);
            yield return WaitDownloads(60f);
            yield return WaitHeroine(12f);
            yield return new WaitForSecondsRealtime(1.5f);
            Diagnose();
            yield return Shoot("main");
            if (_tag.StartsWith("tour")) yield return Tour();
            if (marked != null) LvnProgress.ClearCurrent(marked);
            Done();
        }

        // ── загрузчик ───────────────────────────────────────────────────────
        // Кэш перед запуском стёрт снаружи, так что библиотека греется заново и
        // кружок живой. Разворачиваем его, жмём «Скачать всю игру», даём очереди
        // разогнаться и снимаем лист с графиком.
        private IEnumerator Downloads()
        {
            var root = _hub.panel?.visualTree;
            DownloadHud hud = null;
            for (float t = 0f; t < 40f && hud == null; t += 0.5f)
            {
                var h = root?.Query<DownloadHud>().First();
                if (h != null && h.HasWork) hud = h;
                else yield return new WaitForSecondsRealtime(0.5f);
            }
            if (hud == null) { Debug.LogWarning("[shots] загрузчик: кружок так и не появился"); yield break; }
            Debug.Log("[shots] загрузчик: кружок на месте, разворачиваю");
            Tap(hud.Q(name: "download-capsule"));
            yield return new WaitForSecondsRealtime(3f);
            yield return Shoot("dl-open");
            yield return new WaitForSecondsRealtime(6f);
            yield return Shoot("dl-warm");
            var all = hud.Q(name: "download-all");
            Debug.Log($"[shots] загрузчик: кнопка «всю игру» {(all != null ? "есть" : "нет")}");
            if (all != null)
            {
                Tap(all);
                yield return new WaitForSecondsRealtime(12f);
                yield return Shoot("dl-queue");
                yield return new WaitForSecondsRealtime(25f);
                yield return Shoot("dl-later");
            }
            var st = hud.Q<Label>("download-state"); var pc = hud.Q<Label>("download-percent"); var sp = hud.Q<Label>("download-speed");
            Debug.Log($"[shots] загрузчик: состояние «{st?.text}», процент «{pc?.text}», скорость «{sp?.text}», отдача «{hud.Q<Label>("download-up")?.text}»");
        }

        // ── видео листа загрузок ────────────────────────────────────────────
        // Кадр за кадром в JPG плюс метки времени: ролик собирает ffmpeg по
        // настоящим длительностям, поэтому темп записи на ролик не влияет.
        // Сервер на время съёмки идёт через прокси с узкой полосой (.server),
        // иначе локальная отдача заканчивается раньше, чем лист раскроется.

        private bool _recording;
        private int _frames;

        private IEnumerator Record(string dir)
        {
            Directory.CreateDirectory(dir);
            var times = new System.Text.StringBuilder();
            float t0 = Time.realtimeSinceStartup;
            while (_recording)
            {
                yield return new WaitForEndOfFrame();
                if (!_recording) break;
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var jpg = tex.EncodeToJPG(88);
                Destroy(tex);
                File.WriteAllBytes(Path.Combine(dir, $"{++_frames:00000}.jpg"), jpg);
                times.Append((Time.realtimeSinceStartup - t0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "times.txt"), times.ToString());
            Debug.Log($"[shots] видео: {_frames} кадров за {Time.realtimeSinceStartup - t0:F1} с → {dir}");
        }

        private IEnumerator Video()
        {
            var root = _hub.panel?.visualTree;
            var dir = Path.Combine(OutDir, $"video-{_tag}");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            _recording = true;
            StartCoroutine(Record(dir));
            yield return new WaitForSecondsRealtime(2f);            // главная, кружок в меню

            DownloadHud hud = null;
            for (float t = 0f; t < 40f && hud == null; t += 0.25f)
            {
                var h = root?.Query<DownloadHud>().First();
                if (h != null && h.HasWork) hud = h;
                else yield return new WaitForSecondsRealtime(0.25f);
            }
            if (hud == null) { Debug.LogWarning("[shots] видео: кружок так и не появился"); _recording = false; yield break; }
            Tap(hud.Q(name: "download-capsule"));                  // разворот в лист
            yield return new WaitForSecondsRealtime(9f);            // график живёт

            var all = hud.Q(name: "download-all");
            Debug.Log($"[shots] видео: кнопка «всю игру» {(all != null ? "есть" : "нет")}");
            if (all != null)
            {
                Tap(all);
                yield return new WaitForSecondsRealtime(14f);       // очередь, отказы, докачка
            }
            var scroll = hud.Q<ScrollView>();
            if (scroll != null)
            {
                for (int i = 0; i < 40; i++)                         // прокрутка списка вниз
                {
                    scroll.scrollOffset = new Vector2(0f, scroll.scrollOffset.y + 9f);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(1.5f);
            }
            Tap(hud.Q(name: "download-close"));                    // свернуть в кружок
            yield return new WaitForSecondsRealtime(2.5f);
            Tap(hud.Q(name: "download-capsule"));                  // и снова открыть
            yield return new WaitForSecondsRealtime(4f);
            var st = hud.Q<Label>("download-state"); var pc = hud.Q<Label>("download-percent"); var sp = hud.Q<Label>("download-speed");
            Debug.Log($"[shots] видео: состояние «{st?.text}», процент «{pc?.text}», скорость «{sp?.text}», отдача «{hud.Q<Label>("download-up")?.text}»");
            _recording = false;
            yield return null;
        }

        // ── проверка находок тестировщика 11.09 ─────────────────────────────
        // Семь пунктов Арама и Ильи: имя в шапке, валюта, дверь логотипа,
        // кнопка круток, смена героя, эмоция, скрытый интерфейс главы.
        // Каждый — вердикт в лог и кадр.

        private IEnumerator Qa()
        {
            var bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            VisualElement HubField(string n) => _hub.GetType().GetField(n, bf)?.GetValue(_hub) as VisualElement;
            var shell = _app.Shell;
            var bar = shell.TopBar;
            VisualElement BarField(string n) => bar.GetType().GetField(n, bf)?.GetValue(bar) as VisualElement;
            float CenterY(VisualElement e) => e != null ? e.worldBound.center.y : float.NaN;

            // 1. Имя: игрок назвался — шапка перечиталась сразу, без пересборки.
            string before = Lvn.UI.LvnPlayerName.Display;
            var nameLbl = bar.Query<Label>().Where(l => l.text == before).First();
            string wasName = Lvn.UI.LvnPlayerName.Current;
            Lvn.UI.LvnPlayerName.Set("Виктория");
            yield return null;
            Verdict($"имя в шапке после ввода — «{nameLbl?.text}» (было «{before}»)", nameLbl != null && nameLbl.text == "Виктория");
            yield return Shoot("qa-name");

            // 2. Валюта: значок, число и «плюс» на одной середине, зазоры равные.
            var pills = bar.Query<LvnWalletPill>().ToList();
            bool aligned = pills.Count > 0; string geo = "";
            foreach (var pill in pills)
            {
                if (pill.childCount < 3) { aligned = false; continue; }
                var icon = pill[0]; var amount = pill.Q<Label>(); var plus = pill[pill.childCount - 1];
                float dy = Mathf.Max(Mathf.Abs(CenterY(icon) - CenterY(amount)), Mathf.Abs(CenterY(plus) - CenterY(amount)));
                float gapL = amount.worldBound.xMin - icon.worldBound.xMax, gapR = plus.worldBound.xMin - amount.worldBound.xMax;
                geo += $" [{amount.text}: Δy={dy:F0} зазоры {gapL:F0}/{gapR:F0}]";
                if (dy > 3f || Mathf.Abs(gapL - gapR) > 3f) aligned = false;
            }
            Verdict("валюта: одна середина и равные зазоры" + geo, aligned);

            // 3. Дверь логотипа: аватар открывает профиль, буквы ведут домой.
            Tap(BarField("_stageAvatar"));
            yield return new WaitForSecondsRealtime(1.5f);
            Verdict("тап по аватару открыл профиль", shell.Profile != null && OnScreen(shell.Profile));
            yield return Shoot("qa-profile");
            Tap(bar.Q(name: "stage-logo-door"));
            yield return new WaitForSecondsRealtime(1.5f);
            Verdict("дверь логотипа вернула главную", OnScreen(HubField("_hubView")) && !(shell.Profile != null && OnScreen(shell.Profile)));

            // 4. Кнопка круток на месте.
            var spin = HubField("_stageSpin");
            Verdict($"кнопка круток показана (обработчик {(_hub.OnSpin != null ? "есть" : "нет")})",
                spin != null && spin.resolvedStyle.display == DisplayStyle.Flex && _hub.OnSpin != null);

            // 5. Гардероб: другой герой → в меню одна кукла, а не двое.
            Tap(_hub.Q(name: "stage-tab-" + LvnTabs.Wardrobe));
            yield return new WaitForSecondsRealtime(2.5f);
            var tab = shell.WardrobeTab;
            var sheet = tab?.GetType().GetField("_sheet", bf)?.GetValue(tab) as VisualElement;
            var roster = sheet?.GetType().GetField("_roster", bf)?.GetValue(sheet) as List<(string id, string name)>;
            string firstId = roster != null && roster.Count > 0 ? roster[0].id : null;
            string otherName = roster != null && roster.Count > 1 ? roster[1].name : null;
            var otherTile = otherName != null ? sheet.Query<Button>().Where(b => b.text == otherName || (b.Q<Label>()?.text == otherName)).First() : null;
            Debug.Log($"[shots] тур: ростер {(roster == null ? "нет" : string.Join(", ", roster.ConvertAll(r => r.id + "=" + r.name)))}, плитка другого {(otherTile != null ? "есть" : "нет")}");
            if (otherTile != null)
            {
                Tap(otherTile);
                yield return new WaitForSecondsRealtime(3f);
                yield return Shoot("qa-hero2");
                Tap(_hub.Q(name: "stage-tab-home"));
                yield return new WaitForSecondsRealtime(3f);
                var onStage = _app.Stage != null ? _app.Stage.ActorsOnStage() : new List<string>();
                Verdict($"после смены героя в меню одна кукла ({string.Join(", ", onStage)})", onStage.Count == 1);
                yield return Shoot("qa-hero-home");
                // назад к первому герою — стенд не должен оставаться переключённым
                Tap(_hub.Q(name: "stage-tab-" + LvnTabs.Wardrobe));
                yield return new WaitForSecondsRealtime(2.5f);
                var firstTile = firstId != null ? sheet.Query<Button>().Where(b => b.text == roster[0].name || (b.Q<Label>()?.text == roster[0].name)).First() : null;
                if (firstTile != null) { Tap(firstTile); yield return new WaitForSecondsRealtime(2f); }
            }
            else Verdict("смена героя: второй плитки нет — не проверить", false);

            // 6. Эмоция: пункт → примерка → облик куклы берёт выбранное лицо.
            string entity = sheet?.GetType().GetField("_entity", bf)?.GetValue(sheet) as string;
            string axis = sheet?.GetType().GetField("_emotionAxis", bf)?.GetValue(sheet) as string;
            string current = entity != null && axis != null ? Lvn.UI.LvnCostumer.Chosen(entity, axis, null) : null;
            var def = _app.Stage?.Catalog?.Get(entity);
            var values = def?.axes != null && axis != null && def.axes.TryGetValue(axis, out var vals) ? vals : null;
            string pick = values != null ? values.Find(v => v != current && v != "idle") : null;
            Debug.Log($"[shots] тур: эмоция — сущность {entity ?? "-"}, ось {axis ?? "-"}, сейчас {current ?? "-"}, беру {pick ?? "-"}");
            if (entity != null && axis != null && pick != null)
            {
                string word = LvnWords.Of("emotion." + pick, pick);
                var chip = sheet.Query<Button>().Where(b => b.text == word || b.text == pick).First();
                Tap(chip);
                yield return new WaitForSecondsRealtime(1.5f);
                var look = Lvn.UI.LvnCostumer.Look(new Dictionary<string, string> { [axis] = "idle" }, entity, null);
                Verdict($"эмоция «{pick}»: пункт {(chip != null ? "нажат" : "не найден")}, примерка {Lvn.UI.LvnCostumer.Chosen(entity, axis, null)}, облик даёт {(look.TryGetValue(axis, out var got) ? got : "-")}",
                    chip != null && look.TryGetValue(axis, out var got2) && got2 == pick);
                yield return Shoot("qa-emotion");
                Lvn.UI.LvnWardrobe.ClearPreview(entity);
            }
            else Verdict("эмоция: нет оси или значений — не проверить", false);
            Tap(_hub.Q(name: "stage-tab-home"));
            yield return new WaitForSecondsRealtime(2f);

            // 7. Глава: скрытый интерфейс не накрывает процент и валюту; «Авто» есть и включается.
            Tap(_hub.Q(name: "stage-open-card"));
            yield return new WaitForSecondsRealtime(2f);
            var playWord = LvnWords.Of("hub.play", "Play");
            var playLbl = shell.Detail?.Query<Label>().Where(l => string.Equals(l.text, playWord, System.StringComparison.OrdinalIgnoreCase)).First();
            Tap(playLbl?.parent ?? playLbl);
            for (float t = 0f; t < 40f && !Lvn.UI.LvnScreenDirector.Current.InChapter; t += 0.5f) yield return new WaitForSecondsRealtime(0.5f);
            Verdict("глава открылась по «Играть»", Lvn.UI.LvnScreenDirector.Current.InChapter);
            yield return new WaitForSecondsRealtime(4f);
            Tap(BarField("_tapCatcher"));
            yield return new WaitForSecondsRealtime(1.2f);
            var row = BarField("_gameRow"); var mini = BarField("_miniPills"); var prog = BarField("_miniProgress");
            float lineBottom = Mathf.Max(mini?.worldBound.yMax ?? 0f, prog?.worldBound.yMax ?? 0f);
            Verdict($"ряд кнопок под строкой процента и валюты (ряд с {row?.worldBound.yMin:F0}, строка до {lineBottom:F0})",
                row != null && row.resolvedStyle.display == DisplayStyle.Flex && row.worldBound.yMin >= lineBottom - 1f);
            yield return Shoot("qa-chapter-row");
            var autoWord = LvnWords.Of("game.auto", "Auto");
            var autoLbl = row?.Query<Label>().Where(l => l.text == autoWord).First();
            Tap(autoLbl?.parent);
            yield return new WaitForSecondsRealtime(1f);
            Verdict($"«{autoWord}» в ряду включает авточтение", autoLbl != null && _app.Stage != null && _app.Stage.AutoReading);
            yield return Shoot("qa-auto");
            _app.Stage?.StopAuto();
            Lvn.UI.LvnPlayerName.Set(wasName ?? string.Empty);
        }

        // ── тур по нажатиям ─────────────────────────────────────────────────
        // Кадр главной ничего не говорит о проводке: жмём каждую живую деталь
        // облика и смотрим, куда она ведёт. Итог каждого шага — строкой в лог,
        // «ДА/НЕТ», плюс кадр.

        // ПАЛЬЦЕМ, А НЕ ГОЛЫМ СОБЫТИЕМ: Clickable слушает pointer down/up и
        // требует левую кнопку и позицию внутри элемента; пустое pooled-событие
        // (кнопка −1, позиция 0,0) он молча отбрасывает, а ClickEvent от мыши
        // игнорирует. Собираем события из системного Event с кнопкой 0 и
        // точкой в центре элемента — в координатах панели.
        private static void Tap(VisualElement el)
        {
            if (el == null) { Debug.LogWarning("[shots] тур: элемента для нажатия нет"); return; }
            var pos = el.worldBound.center;
            var sysDown = new Event { type = EventType.MouseDown, button = 0, mousePosition = pos, clickCount = 1 };
            using (var down = PointerDownEvent.GetPooled(sysDown)) { down.target = el; el.SendEvent(down); }
            var sysUp = new Event { type = EventType.MouseUp, button = 0, mousePosition = pos, clickCount = 1 };
            using (var up = PointerUpEvent.GetPooled(sysUp)) { up.target = el; el.SendEvent(up); }
        }

        // «НА ЭКРАНЕ» — целиком внутри панели, а не у левого края: на планшете
        // оболочка стоит полосой по центру (ScreenUi.PhoneColumn), и витрина
        // с x=996 — на месте, а не «не въехала». Переезд между комнатами
        // уводит экран за край панели — это и ловим.
        private static bool OnScreen(VisualElement e)
        {
            if (e == null || e.panel == null || e.resolvedStyle.display == DisplayStyle.None) return false;
            var wb = e.worldBound;
            float panelW = e.panel.visualTree.worldBound.width;
            return wb.width > 1f && wb.xMin > -1f && wb.xMax < panelW + 1f && e.resolvedStyle.opacity > 0.5f;
        }

        private void Verdict(string what, bool ok) => Debug.Log($"[shots] тур: {what} — {(ok ? "ДА" : "НЕТ")}");

        private IEnumerator Tour()
        {
            var bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            VisualElement Field(string n) => _hub.GetType().GetField(n, bf)?.GetValue(_hub) as VisualElement;
            var shell = _app.Shell;

            // 0. Награда за рекламу — первой, пока экран чист: заглушка показа
            // отвечает «да», сервер начисляет, кошелёк растёт.
            var placement = _hub.AdPlacement;
            var adState = string.IsNullOrEmpty(placement) ? null : Lvn.Services.LvnAds.StateOf(placement);
            Debug.Log($"[shots] тур: реклама — площадка «{placement ?? "-"}», показ доступен={Lvn.Services.LvnAds.Available}, "
                    + $"состояние={(adState != null ? adState.Amount.ToString() : "нет")}, кнопка={(_hub.Q(name: "stage-ad")?.resolvedStyle.display == DisplayStyle.Flex ? "показана" : "скрыта")}");
            int changed = 0; System.Action onAds = () => changed++;
            Lvn.Services.LvnAds.Changed += onAds;
            long before = Lvn.Services.LvnWallet.Balance("crystals");
            var adBtn = _hub.Q(name: "stage-ad");
            int downs = 0, ups = 0;
            adBtn?.RegisterCallback<PointerDownEvent>(e => downs++);
            adBtn?.RegisterCallback<PointerUpEvent>(e => ups++);
            Tap(adBtn);
            yield return new WaitForSecondsRealtime(1f);
            Debug.Log($"[shots] тур: реклама — до кнопки дошло down={downs} up={ups}, bound={adBtn?.worldBound}, "
                    + $"pick={adBtn?.pickingMode}, enabled={adBtn?.enabledInHierarchy}, под центром={adBtn?.panel?.Pick(adBtn.worldBound.center)?.name}");
            yield return new WaitForSecondsRealtime(3f);
            Lvn.Services.LvnAds.Changed -= onAds;
            long after = Lvn.Services.LvnWallet.Balance("crystals");
            Verdict($"реклама начислила кристаллы по нажатию ({before} → {after}, ответов сервера {changed})", after > before);
            if (after <= before)
            {
                // Разделяем «нажатие не дошло» и «тракт награды не работает»:
                // зовём тракт напрямую, минуя кнопку.
                var direct = Lvn.Services.LvnAds.WatchAndRewardAsync(placement);
                for (float t = 0f; t < 6f && !direct.IsCompleted; t += 0.25f) yield return new WaitForSecondsRealtime(0.25f);
                long after2 = Lvn.Services.LvnWallet.Balance("crystals");
                Verdict($"реклама напрямую, минуя кнопку: тракт ответил {(direct.IsCompleted ? direct.Result.ToString() : "не завершился")} ({after} → {after2})", after2 > after);
            }

            // 1. Панель сообщений → библиотека (список всех новелл).
            Tap(_hub.Q(name: "stage-open-panel"));
            yield return new WaitForSecondsRealtime(2.5f);
            Verdict("панель «Открыть» открыла библиотеку", OnScreen(Field("_collectionView")));
            yield return Shoot("library");
            _hub.GetType().GetMethod("ShowHub", bf)?.Invoke(_hub, null);
            yield return new WaitForSecondsRealtime(1f);

            // 2. Карточка «Открыть» → деталь новеллы (экран хоста).
            Tap(_hub.Q(name: "stage-open-card"));
            yield return new WaitForSecondsRealtime(3f);
            bool detail = shell.Detail != null && OnScreen(shell.Detail);
            Verdict("карточка «Открыть» открыла деталь новеллы", detail);
            yield return Shoot("detail");
            if (detail)
            {
                var hide = shell.Detail.GetType().GetMethod("Hide", bf, null, System.Type.EmptyTypes, null);
                if (hide != null) hide.Invoke(shell.Detail, null);
                else Tap(shell.Detail.Query<Button>().First());
                yield return new WaitForSecondsRealtime(1.5f);
            }

            // 4. Вкладка «Магазин» → лента уезжает на магазин; центр → домой.
            Tap(_hub.Q(name: "stage-tab-" + LvnTabs.Store));
            yield return new WaitForSecondsRealtime(2.5f);
            Verdict("вкладка «Магазин» показала магазин", shell.PackShop != null && OnScreen(shell.PackShop));
            yield return Shoot("store");
            Tap(_hub.Q(name: "stage-tab-home"));
            yield return new WaitForSecondsRealtime(2.5f);
            Verdict("центральная кнопка вернула главную", OnScreen(Field("_hubView")));

            // 5. Вкладка «Гардероб» → гардероб; домой.
            Tap(_hub.Q(name: "stage-tab-" + LvnTabs.Wardrobe));
            yield return new WaitForSecondsRealtime(3f);
            Verdict("вкладка «Гардероб» показала гардероб", shell.WardrobeTab != null && OnScreen(shell.WardrobeTab));
            yield return Shoot("wardrobe");
            Tap(_hub.Q(name: "stage-tab-home"));
            yield return new WaitForSecondsRealtime(2.5f);
            Verdict("возврат домой из гардероба", OnScreen(Field("_hubView")));
            yield return Shoot("home-again");
        }
    }
}
#endif
