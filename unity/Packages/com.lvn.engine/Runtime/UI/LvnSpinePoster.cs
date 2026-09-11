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
    public static class LvnSpinePoster
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
            Action<RenderTexture> onPoster = null)
        {
            if (host == null || spine == null || loadText == null || loadSprite == null) return;
            if (!LvnSpineBridge.Available) { onFallback?.Invoke(); return; }
            var origin = new Vector3(20000f + (_seq++ % 64) * Spacing, 20000f, 0f);
            LvnAsync.Fire(BuildAsync(host, spine, loadText, loadSprite, origin, ledger, onFallback, onPoster),
                "SpinePoster");
        }

        /// <summary>ПРОГРЕТЬ спайн заранее: те же файлы теми же загрузчиками (то
        /// есть в ТОТ ЖЕ кэш, что потом спросит <see cref="Attach"/>) плюс разбор
        /// скелета в стороне от главного потока. Зовётся на буте, чтобы карточка
        /// ленты показала фигуру сразу. Лучшее усилие: не вышло — Attach просто
        /// загрузит сам, как раньше.</summary>
        public static async Task WarmAsync(LvnSpineRef spine,
            Func<string, Task<string>> loadText,
            Func<string, Task<Sprite>> loadSprite)
        {
            if (spine == null || loadText == null || loadSprite == null) return;
            var kit = await LoadKitAsync(spine, loadText, loadSprite);
            if (!kit.Ok || LvnSpineBridge.Prepare == null) return;
            // Разбор json стоит 100-170 мс на главном потоке. Мост умеет сделать
            // его заранее и в стороне; без прогрева они пришлись бы ровно на тот
            // кадр, в котором игрок открывает ленту.
            try { await LvnSpineBridge.Prepare(kit.Json, kit.Atlas, kit.Textures); } catch { }
        }

        // Файлы одного скелета. ОБЩИЙ ДОМ для прогрева и сборки нарочно: разойдись
        // они — прогрев грел бы один набор адресов, а карточка спрашивала другой,
        // и «моментально» тихо перестало бы работать, не сломав ничего видимого.
        private struct Kit
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

        private static async Task<Kit> LoadKitAsync(LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite)
        {
            var kit = new Kit();
            kit.Json = await loadText(spine.json);
            string atlasText = null;
            try { atlasText = await loadText(spine.atlas); }
            catch { }
            if (string.IsNullOrEmpty(atlasText))
            {
                var alt = AtlasWithoutTxt(spine.atlas);
                if (!string.IsNullOrEmpty(alt)) { try { atlasText = await loadText(alt); spine.atlas = alt; } catch { } }
            }
            if (string.IsNullOrEmpty(kit.Json) || string.IsNullOrEmpty(atlasText)) return kit;
            kit.Atlas = atlasText;

            var textures = new List<Texture2D>();
            var sprites = new List<Sprite>();
            foreach (var url in PageUrls(spine.atlas, atlasText, spine.texture))
            {
                Sprite spr = null;
                try { spr = await loadSprite(url); } catch { }
                var tex = LiveTexture(spr);
                if (tex != null) { textures.Add(tex); sprites.Add(spr); }
            }
            kit.Textures = textures.ToArray();
            kit.Sprites = sprites;

            if (!string.IsNullOrEmpty(spine.bg))
            {
                Sprite bg = null;
                try { bg = await loadSprite(spine.bg); } catch { }
                kit.Bg = LiveTexture(bg);
                if (kit.Bg != null) sprites.Add(bg);
            }
            return kit;
        }

        private static async Task BuildAsync(VisualElement host, LvnSpineRef spine,
            Func<string, Task<string>> loadText, Func<string, Task<Sprite>> loadSprite, Vector3 origin,
            Lvn.Content.ILvnPinLedger ledger, Action onFallback, Action<RenderTexture> onPoster)
        {
            var kit = await LoadKitAsync(spine, loadText, loadSprite);
            // НЕ ВЫШЛО — ЗОВЁМ ЗАПАСНОЙ ХОД. Молчаливый выход оставлял карточку
            // пустым прямоугольником: обложку на спайн-карточке не ставят, а
            // фигура не приехала — показывать было нечего.
            if (!kit.Ok) { onFallback?.Invoke(); return; }
            if (host.panel == null) return; // элемент исчез, пока грузили

            // ЗАКРЕПЛЯЕМ СТРАНИЦЫ. Скелет держит их текстуры в своём материале, а
            // стриминговое окно про это не знает: выгруженная страница делает
            // фигуру чёрной/розовой без шанса восстановиться.
            //
            // ДЕРЖИТ ДОСКА, а не мы сами: правило «прикрепить новое раньше, чем
            // отпустить прежнее» живёт у неё одной. Наборы страниц у постеров
            // пересекаются (один атлас на всех), и отпустив первым, доводишь
            // счётчик общего спрайта до нуля — окно вправе забрать текстуру
            // ровно в этот миг.
            _pins.Hold(host, ledger, kit.Sprites);

            // ── офф-скрин установка: корень в своём углу мира ─────────────────
            var root = new GameObject("lvn-spine-poster");
            root.transform.position = origin;

            var canvasGo = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            canvasGo.transform.SetParent(root.transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var crt = canvasGo.GetComponent<RectTransform>();
            // ХОЛСТ ПОВТОРЯЕТ ФОРМУ САМОГО ЭЛЕМЕНТА. Держать здесь свою форму
            // (аспект фигуры) значило подать в постер картинку другой формы — и
            // она либо оставляла полосы (contain), либо срезалась по краю
            // (cover). Совпали формы — срезать и добирать нечего: спайн ложится
            // в ровень. Размер элемента к этому мигу обычно уже посчитан; не
            // успел (замерено — бывает) — отступаем на аспект скелета.
            const float ch = 800f;
            float hw = host.resolvedStyle.width, hh = host.resolvedStyle.height;
            float aspect = (hw > 1f && hh > 1f) ? hw / hh : 0.8325f;
            float cw = Mathf.Round(ch * Mathf.Clamp(aspect, 0.3f, 3f));
            crt.sizeDelta = new Vector2(cw, ch);
            crt.position = origin;

            var rt = new RenderTexture((int)cw, (int)ch, 16, RenderTextureFormat.ARGB32)
            {
                name = "lvn-spine-rt",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();

            var camGo = new GameObject("cam", typeof(Camera));
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = ch * 0.5f;
            cam.transform.position = origin + new Vector3(0f, 0f, -100f);
            cam.transform.rotation = Quaternion.identity;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.targetTexture = rt;
            cam.allowHDR = false; cam.allowMSAA = false;
            // СНИМАЕМ РЕЖЕ, ЧЕМ ИДУТ КАДРЫ. Камера с целевой текстурой рисует
            // КАЖДЫЙ кадр, и на экране их несколько сразу (лента, витрина,
            // столбик магазина): фигура в интерфейсе — не игровая сцена, её
            // движение читается и вполовину реже, а кадры игре нужнее
            // («что-то сжирает кадры, давай спайн ограничим» — Илья 09.09).
            cam.enabled = false;
            var driver = camGo.AddComponent<Ticker>();
            driver.Camera = cam;

            var go = LvnSpineBridge.Create(crt, kit.Json, kit.Atlas, kit.Textures, spine.scale, kit.Bg);
            if (go == null) { Cleanup(root, rt, host); onFallback?.Invoke(); return; }
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
            onPoster?.Invoke(rt);
            // COVER: фигура заполняет постер В РОВЕНЬ, без полей сверху и снизу
            // (Илья). Contain вписывал целиком и оттого оставлял полосы. Cover
            // масштабирует РАВНОМЕРНО и срезает лишнее по краю — пропорции
            // сохраняются так же, растяжки нет.
            host.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);

            var pinned = kit.Sprites;
            EventCallback<DetachFromPanelEvent> onDetach = null;
            onDetach = _ => { host.UnregisterCallback(onDetach); Cleanup(root, rt, host); };
            host.RegisterCallback(onDetach);
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

        /// <summary>Часовой постера: держит камеру выключенной и сам зовёт
        /// съёмку с частотой <see cref="Hz"/>. Живёт на объекте камеры и умирает
        /// вместе с установкой, поэтому гасить его отдельно нечем и незачем.</summary>
        internal sealed class Ticker : MonoBehaviour
        {
            public Camera Camera;
            private float _last = -1f;

            private void LateUpdate()
            {
                // ПОСЛЕ анимации: скелет считает позу в Update, снимать раньше
                // значило бы показывать кадр отставшим на один.
                if (Camera == null) { Destroy(this); return; }
                float now = Time.unscaledTime;
                if (!ShouldRender(_last, now)) return;
                _last = now;
                Camera.Render();
            }
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
}
