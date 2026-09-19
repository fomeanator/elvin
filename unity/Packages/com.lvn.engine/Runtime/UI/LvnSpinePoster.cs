using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ЖИВОЙ СПАЙН КАК ФОН UITK-ЭЛЕМЕНТА (карточки новелл в ленте и т.п.).
    ///
    /// <para>Хаб — это UI Toolkit (<see cref="VisualElement"/>), а спайн — это
    /// uGUI (<c>SkeletonGraphic</c> на Canvas). Внутрь UITK-элемента uGUI не
    /// положить, поэтому мост один: скелет рисуется на СВОЁМ офф-скрин
    /// Canvas'е, отдельная камера снимает его в <see cref="RenderTexture"/>, а
    /// RT ставится фоном элемента (<c>Background.FromRenderTexture</c>, приём
    /// <c>UiGlass</c>). Прокрутку/клип/скругление элемента UITK берёт даром.</para>
    ///
    /// <para>ПО СПАЙНУ НА ЭЛЕМЕНТ (Илья: «у каждой главы свой спайн, много —
    /// норма»): каждый <see cref="Attach"/> поднимает свою установку. Камера и
    /// Canvas унесены далеко в мир (со сдвигом на номер установки), так что
    /// каждая ортокамера видит только свой холст — отдельный слой не нужен.
    /// Живёт, пока жив элемент: на <c>DetachFromPanel</c> камера, RT и скелет
    /// уничтожаются.</para>
    ///
    /// <para>МОМЕНТАЛЬНОСТЬ ДЕРЖИТ <see cref="WarmAsync"/>. Сама установка встаёт
    /// за пару кадров, но файлы скелета (json в мегабайты, атлас, страницы) едут
    /// по сети, и ВСЁ ЭТО ВРЕМЯ ПОСТЕР ПУСТ — карточка выглядит сломанной, хотя
    /// просто грузится (замерено 07.09: холст, текстура и подгонка были в
    /// порядке, не хватало только файлов). Прогрев на буте кладёт файлы в тот же
    /// кэш и разбирает скелет заранее, поэтому к появлению ленты сборке остаётся
    /// только меш.</para>
    ///
    /// <para>Загрузчики файлов приняты делегатами: компонент в движке, не завязан
    /// на фасад ассетов оболочки; спайн-типов здесь нет (мост — делегаты), пакет
    /// собирается и без spine-unity.</para>
    /// </summary>
    public static partial class LvnSpinePoster
    {
        /// <summary>Кто держит страницы атласа: ключ — элемент-хозяин постера.
        /// Одна доска на приложение, потому что и окно памяти одно.</summary>
        private static readonly Lvn.UI.LvnPinBoard<VisualElement> _pins
            = new Lvn.UI.LvnPinBoard<VisualElement>();

        private static int _seq;                 // номер установки → свой угол мира
        private const float Spacing = 5000f;     // разнос установок, чтобы камеры не видели чужой холст

        /// <summary>Повесить живой спайн фоном на <paramref name="host"/>. Нет
        /// spine-unity или файлы не пришли — тихо ничего (хост сохраняет прежний
        /// фон). Загрузчики (текст json/atlas и спрайт страниц/фона) даёт зовущий.</summary>
        public static void Attach(VisualElement host, LvnSpineRef spine,
            Func<string, Task<string>> loadText,
            Func<string, Task<Sprite>> loadSprite,
            Lvn.Content.ILvnPinLedger ledger = null,
            Action onFallback = null,
            Action<RenderTexture> onPoster = null,
            VisualElement visibilityTarget = null)
        {
            if (host == null || spine == null || loadText == null || loadSprite == null) return;
            if (!LvnSpineBridge.Available) { onFallback?.Invoke(); return; }
            AttachOwned(host, spine, loadText, loadSprite, ledger, onFallback, onPoster, visibilityTarget ?? host);
        }

        /// <summary>ПРОГРЕТЬ спайн заранее: те же файлы теми же загрузчиками (то
        /// есть в ТОТ ЖЕ кэш, что потом спросит <see cref="Attach"/>) плюс разбор
        /// скелета в стороне от главного потока. Зовётся на буте, чтобы карточка
        /// ленты показала фигуру сразу. Лучшее усилие: не вышло — Attach просто
        /// загрузит сам, как раньше.</summary>
        public static async Task WarmAsync(LvnSpineRef spine,
            Func<string, Task<string>> loadText,
            Func<string, Task<Sprite>> loadSprite, ILvnPinLedger ledger = null)
        {
            if (spine == null || loadText == null || loadSprite == null) return;
            using var loading = new LoadingPins(ledger);
            var kit = await LoadKitAsync(spine, loadText, loadSprite, loading.Hold);
            if (!kit.Ok || LvnSpineBridge.Prepare == null) return;
            // Разбор json стоит 100-170 мс на главном потоке. Мост умеет сделать
            // его заранее и в стороне; без прогрева они пришлись бы ровно на тот
            // кадр, в котором игрок открывает ленту.
            try { await LvnSpineBridge.Prepare(kit.Json, kit.Atlas, kit.Textures); }
            catch { }   // прогрев — ускорение, а не условие показа: без него фигура соберётся позже
        }

        // Файлы одного скелета. ОБЩИЙ ДОМ для прогрева и сборки нарочно: разойдись
        // они — прогрев грел бы один набор адресов, а карточка спрашивала другой,
        // и «моментально» тихо перестало бы работать, не сломав ничего видимого.
        internal struct Kit
        {
            public string Json, Atlas;
            public Texture2D[] Textures;
            public Texture2D Bg;
            public List<Sprite> Sprites;   // их закрепляем на время жизни установки
            public bool Ok => !string.IsNullOrEmpty(Json) && !string.IsNullOrEmpty(Atlas)
                              && Textures != null && Textures.Length > 0;
        }

        // Живой спрайт: у Unity уничтоженный объект СРАВНИВАЕТСЯ с null, но
        // обращение к его полю бросает MissingReferenceException — а страницы
        // атласа уборка контента уносит (замер 07.09: сборка падала ровно здесь,
        // и карточка оставалась пустой).
        private static Texture2D LiveTexture(Sprite s)
        {
            try { return s == null ? null : s.texture; }
            catch { return null; }
        }

        internal static async Task<Kit> LoadKitAsync(LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite, Action<Sprite> pin = null)
        {
            var kit = new Kit();
            kit.Json = await loadText(spine.json);
            string atlasText = null;
            try { atlasText = await loadText(spine.atlas); }
            catch { }   // имя атласа угадываем: вторая попытка ниже, крик тут был бы ложной тревогой
            if (string.IsNullOrEmpty(atlasText))
            {
                var alt = AtlasWithoutTxt(spine.atlas);
                if (!string.IsNullOrEmpty(alt))
                {
                    try { atlasText = await loadText(alt); }
                    catch { }   // вторая догадка об имени; нет и её — откатимся на обложку
                }
            }
            if (string.IsNullOrEmpty(kit.Json) || string.IsNullOrEmpty(atlasText)) return kit;
            kit.Atlas = atlasText;

            var textures = new List<Texture2D>();
            var sprites = new List<Sprite>();
            foreach (var url in PageUrls(spine.atlas, atlasText, spine.texture))
            {
                Sprite spr = null;
                try { spr = await loadSprite(url); }
                catch { }   // страница атласа не доехала: пустая отсеется ниже, покажем что есть
                var tex = LiveTexture(spr);
                if (tex != null) { pin?.Invoke(spr); textures.Add(tex); sprites.Add(spr); }
            }
            kit.Textures = textures.ToArray();
            kit.Sprites = sprites;

            if (!string.IsNullOrEmpty(spine.bg))
            {
                Sprite bg = null;
                try { bg = await loadSprite(spine.bg); }
                catch { }   // подложка необязательна: без неё фигура стоит на своём фоне
                kit.Bg = LiveTexture(bg);
                if (kit.Bg != null) { pin?.Invoke(bg); sprites.Add(bg); }
            }
            return kit;
        }

        private static async Task BuildAsync(Attachment owner, int revision, LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite)
        {
            using var loading = new LoadingPins(owner.Ledger);
            try
            {
                var kit = await LoadKitAsync(spine, loadText, loadSprite, loading.Hold);
                if (!owner.Current(revision)) return;
                var host = owner.Host;
                // НЕ ВЫШЛО — ЗОВЁМ ЗАПАСНОЙ ХОД. Молчаливый выход оставлял карточку
                // пустым прямоугольником: обложку на спайн-карточке не ставят, а
                // фигура не приехала — показывать было нечего.
                if (!kit.Ok) { owner.Fallback(); return; }
                if (host.panel == null) { owner.Dispose(); return; } // элемент исчез, пока грузили

                // ЗАКРЕПЛЯЕМ СТРАНИЦЫ. Скелет держит их текстуры в своём материале, а
                // стриминговое окно про это не знает: выгруженная страница делает
                // фигуру чёрной/розовой без шанса восстановиться.
                //
                // ДЕРЖИТ ДОСКА, а не мы сами: правило «прикрепить новое раньше, чем
                // отпустить прежнее» живёт у неё одной. Наборы страниц у постеров
                // пересекаются (один атлас на всех), и отпустив первым, доводишь
                // счётчик общего спрайта до нуля — окно вправе забрать текстуру
                // ровно в этот миг.
                _pins.Hold(host, owner.Ledger, kit.Sprites);
                owner.DropRig();

                // ── офф-скрин установка: корень в своём углу мира ─────────────────
                const float ch = 800f;
                float hw = host.resolvedStyle.width, hh = host.resolvedStyle.height;
                float aspect = (hw > 1f && hh > 1f) ? hw / hh : 0.8325f;
                float cw = Mathf.Round(ch * Mathf.Clamp(aspect, 0.3f, 3f));
                var origin = new Vector3(20000f + (_seq++ % 64) * Spacing, 20000f, 0f);
                var rig = BuildRig("lvn-spine-poster", origin, (int)cw, (int)ch);
                owner.Rig = rig; // also owns partial builds when the bridge throws
                rig.Ticker.VisibilityTarget = owner.VisibilityTarget;
                var crt = rig.Canvas; var rt = rig.Rt;

                var go = LvnSpineBridge.Create(crt, kit.Json, kit.Atlas, kit.Textures, spine.scale, kit.Bg);
                if (go == null) { owner.Fallback(); return; }
                if (LvnSpineBridge.SetVisible != null) LvnSpineBridge.SetVisible(go, true);
                // И ВНУТРИ холста тоже в ровень: подгонка по ширине оставляла бы
                // фигуру в рамке пустоты, а её потом видно как те же полосы —
                // срезать на композите было бы нечего.
                if (LvnSpineBridge.Refit != null) LvnSpineBridge.Refit(go, spine.scale, "cover");
                if (!string.IsNullOrEmpty(spine.auto) && LvnSpineBridge.Play != null)
                    LvnSpineBridge.Play(go, spine.auto, true);

                host.style.backgroundImage = Background.FromRenderTexture(rt);
                // ТЕКСТУРУ ОТДАЁМ В РУКИ, А НЕ ЧЕРЕЗ СТИЛЬ. Тот, кто хочет разделить
                // постер на несколько элементов, не может прочитать её обратно из
                // style.backgroundImage: геттер UITK собирает Background из
                // Texture2D/Sprite/VectorImage и RenderTexture теряет — читалось
                // пусто, и панели магазина стояли без фигуры (Илья 08.09).
                owner.OnPoster?.Invoke(rt);
                // COVER: фигура заполняет постер В РОВЕНЬ, без полей сверху и снизу
                // (Илья). Contain вписывал целиком и оттого оставлял полосы. Cover
                // масштабирует РАВНОМЕРНО и срезает лишнее по краю — пропорции
                // сохраняются так же, растяжки нет.
                host.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);

                LvnLog.Trace($"[lvn-poster] ready json={spine.json} bg={spine.bg} rt={rt.width}x{rt.height} hosts={_attachments.Count}");
            }
            catch
            {
                if (owner.Current(revision)) owner.Fallback();
                throw;
            }
        }

        /// <summary>Сколько раз в секунду постер переснимает фигуру. Экран
        /// обновляется чаще, но фигура в интерфейсе не игровая сцена: движение
        /// на 30 кадрах читается тем же, а половина работы уходит. Число одно
        /// на все постеры — их на экране бывает пять.</summary>
        public const float Hz = 30f;

        /// <summary>Пора ли снимать заново. Чистая функция, чтобы правило было
        /// проверяемо без камеры и кадров (и чтобы «раз в 1/30» не разъехалось
        /// с тем, что делает <see cref="Ticker"/>).</summary>
        public static bool ShouldRender(float lastRender, float now)
            => lastRender < 0f || now - lastRender >= 1f / Hz;

        /// <summary>ЗАКАДРОВЫЙ РИГ — один на постер и живой фон: корень в
        /// дальнем углу мира, холст в мировом пространстве размером с кадр,
        /// текстура того же размера и ортокамера с тикером. Раньше собирался
        /// дважды, строка в строку; расхождение (например, формат текстуры)
        /// ушло бы в один из двух молча.</summary>
        internal sealed class Rig
        {
            public GameObject Root;
            public RectTransform Canvas;
            public RenderTexture Rt;
            public Camera Camera;
            public Ticker Ticker;
        }

        internal static Rig BuildRig(string name, Vector3 origin, int width, int height)
        {
            var root = new GameObject(name);
            root.transform.position = origin;
            var canvasGo = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            canvasGo.transform.SetParent(root.transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var crt = canvasGo.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(width, height);
            crt.position = origin;
            var rt = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
            {
                name = name + "-rt",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            var camGo = new GameObject("cam", typeof(Camera));
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = height * 0.5f;
            cam.transform.position = origin + new Vector3(0f, 0f, -100f);
            cam.transform.rotation = Quaternion.identity;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.targetTexture = rt;
            // Produce the texture before the scene camera (depth -50) samples it.
            cam.depth = -100f;
            cam.allowHDR = false; cam.allowMSAA = false;
            cam.enabled = false;
            var driver = camGo.AddComponent<Ticker>();
            driver.Camera = cam;
            driver.CanvasRoot = canvasGo;
            return new Rig { Root = root, Canvas = crt, Rt = rt, Camera = cam, Ticker = driver };
        }

        private static void Cleanup(GameObject root, RenderTexture rt, VisualElement host)
        {
            // Отпускает доска — тем же ledger'ом, которым держала: каждое
            // закрепление обязано получить своё освобождение, иначе страницы
            // атласа останутся в памяти навсегда.
            if (host != null) _pins.Release(host);
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            if (root != null) UnityEngine.Object.Destroy(root);
        }

        /// <summary>Адреса страниц атласа по порядку файла, рядом с атласом:
        /// libgdx-атлас называет каждую страницу отдельной строкой на .png.
        /// Нет ни одной — запасная текстура каталога. Один дом на постер и
        /// сцену (VnStage.Spine): копии расходятся.</summary>
        internal static List<string> PageUrls(string atlasUrl, string atlasText, string fallback)
        {
            var urls = new List<string>();
            string dir = "";
            int slash = atlasUrl != null ? atlasUrl.LastIndexOf('/') : -1;
            if (slash >= 0) dir = atlasUrl.Substring(0, slash + 1);
            if (!string.IsNullOrEmpty(atlasText))
                foreach (var raw in atlasText.Split('\n'))
                {
                    var line = raw.Trim();
                    // РОД ФАЙЛА СПРАШИВАЕМ У ДОМА. Свой список расширений знал
                    // только png — атлас со страницей в jpg (Spine так тоже
                    // умеет) разбирался в пустоту, молча и без ошибки.
                    if (Lvn.Content.DownloadPolicy.IsImage(line)) urls.Add(dir + line);
                }
            if (urls.Count == 0 && !string.IsNullOrEmpty(fallback)) urls.Add(fallback);
            return urls;
        }

        private static string AtlasWithoutTxt(string atlas)
            => string.IsNullOrEmpty(atlas) || !atlas.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase)
                ? null : atlas.Substring(0, atlas.Length - ".txt".Length);
    }

    /// <summary>
    /// ЖИВОЙ ФОН СЦЕНЫ — спайн-сцена в текстуре (TR-132, TR-133).
    ///
    /// <para>Скелет как актёр сцены на эмуляторе Ильи собирался, но не
    /// выводился (рентген 16.09: у рендерера нет материала), а тот же скелет в
    /// постере карточки рисуется. Поэтому фон идёт дорогой постера и
    /// 3D-задника: свой закадровый холст, камера, текстура — и эта текстура
    /// ложится на полотно сцены (<c>WorldBackground.SetLiveTexture</c>). Ни
    /// слотов, ни порядка по z, ни въездов — это фон, а не фигура.</para>
    /// </summary>
    public static class LvnSpineBackdrop
    {
        private static readonly LvnPinBoard<object> _pins = new LvnPinBoard<object>();
        private static int _seq;
        private const float Spacing = 5000f;

        /// <summary>Живой фон на руках у сцены: текстура и снятие.</summary>
        public sealed class Handle
        {
            internal GameObject Root;
            internal RenderTexture Rt;
            internal object PinKey;
            internal LvnSpinePoster.Ticker Ticker;
            public bool Released { get; private set; }
            public bool Paused { get; private set; }
            public RenderTexture Texture => Released ? null : Rt;

            /// <summary>ПАУЗА: сцена остаётся собранной (скелет, страницы,
            /// текстура), но закадровая камера не рисует. Так полотно меню
            /// держит пару последних сцен наготове, а не пересобирает
            /// 1080×1920 при каждом переключении вкладки («лагает жуть» —
            /// Илья 17.09: каждая пересборка — секундный провал).</summary>
            public void Pause() { Paused = true; if (Ticker != null) Ticker.AlwaysVisible = false; }
            public void Resume() { Paused = false; if (Ticker != null) Ticker.AlwaysVisible = true; }
            public void Release()
            {
                if (Released) return;
                Released = true;
                if (PinKey != null) _pins.Release(PinKey);
                if (Rt != null) { Rt.Release(); UnityEngine.Object.Destroy(Rt); Rt = null; }
                if (Root != null) { UnityEngine.Object.Destroy(Root); Root = null; }
            }
        }

        /// <summary>ПАРК СОБРАННЫХ СЦЕН: снятая сцена стоит на паузе и ждёт
        /// возврата — вместо пересборки холста, камеры, текстуры и страниц 2K
        /// при каждом переключении вкладки («лагает жуть» — Илья 17.09).
        /// Вместимость мала намеренно: текстуры во весь экран и страницы 2K.</summary>
        public sealed class Park
        {
            private readonly List<(string key, Handle handle)> _items = new List<(string, Handle)>();
            private readonly int _capacity;
            public Park(int capacity) { _capacity = Mathf.Max(1, capacity); }

            /// <summary>Забрать сцену по ключу (пусто — нет или уже снесена); из парка она уходит.</summary>
            public Handle Take(string key)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].key != key) continue;
                    var h = _items[i].handle;
                    _items.RemoveAt(i);
                    return h == null || h.Released ? null : h;
                }
                return null;
            }

            /// <summary>Поставить сцену в парк на паузу; самая давняя сверх вместимости сносится.</summary>
            public void Put(string key, Handle handle)
            {
                if (handle == null || handle.Released || string.IsNullOrEmpty(key)) { handle?.Release(); return; }
                handle.Pause();
                _items.Add((key, handle));
                while (_items.Count > _capacity)
                {
                    _items[0].handle?.Release();
                    _items.RemoveAt(0);
                }
            }

            public void Clear()
            {
                foreach (var it in _items) it.handle?.Release();
                _items.Clear();
            }
        }

        /// <summary>Собрать сцену закадрово и отдать текстуру, когда она готова.
        /// <paramref name="width"/>/<paramref name="height"/> — логический кадр
        /// сцены; текстура рисуется той же формы, чтобы полотно не кадрировало.</summary>
        public static Handle Attach(LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite,
            int width, int height, Lvn.Content.ILvnPinLedger ledger,
            Action<RenderTexture> onTexture, Action onFallback = null)
        {
            var handle = new Handle { PinKey = new object() };
            if (spine == null || loadText == null || loadSprite == null || width < 8 || height < 8)
            { handle.Release(); onFallback?.Invoke(); return handle; }
            if (!LvnSpineBridge.Available) { handle.Release(); onFallback?.Invoke(); return handle; }
            var origin = new Vector3(-20000f - (_seq++ % 64) * Spacing, -20000f, 0f);
            LvnAsync.Fire(BuildAsync(handle, spine, loadText, loadSprite, origin, width, height, ledger, onTexture, onFallback), "SpineBackdrop");
            return handle;
        }

        private static async Task BuildAsync(Handle handle, LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite, Vector3 origin,
            int width, int height, Lvn.Content.ILvnPinLedger ledger, Action<RenderTexture> onTexture, Action onFallback)
        {
            using var loading = new LvnSpinePoster.LoadingPins(ledger);
            try
            {
                var kit = await LvnSpinePoster.LoadKitAsync(spine, loadText, loadSprite, loading.Hold);
                if (handle.Released) return;
                if (!kit.Ok) { handle.Release(); onFallback?.Invoke(); return; }
                _pins.Hold(handle.PinKey, ledger, kit.Sprites);

                var rig = LvnSpinePoster.BuildRig("lvn-spine-backdrop", origin, width, height);
                rig.Ticker.AlwaysVisible = !handle.Paused;   // поставлен на паузу до сборки — не рисуем и после
                handle.Root = rig.Root; handle.Rt = rig.Rt; handle.Ticker = rig.Ticker;
                var crt = rig.Canvas; var rt = rig.Rt;
                var go = LvnSpineBridge.Create(crt, kit.Json, kit.Atlas, kit.Textures, spine.scale, kit.Bg);
                if (go == null) { handle.Release(); onFallback?.Invoke(); return; }
                if (LvnSpineBridge.SetVisible != null) LvnSpineBridge.SetVisible(go, true);
                if (LvnSpineBridge.Refit != null) LvnSpineBridge.Refit(go, spine.scale, "cover");
                if (!string.IsNullOrEmpty(spine.auto) && LvnSpineBridge.Play != null)
                    LvnSpineBridge.Play(go, spine.auto, true);
                if (handle.Released) return;
                onTexture?.Invoke(rt);
            }
            catch
            {
                if (!handle.Released) { handle.Release(); onFallback?.Invoke(); }
                throw;
            }
        }
    }
}
