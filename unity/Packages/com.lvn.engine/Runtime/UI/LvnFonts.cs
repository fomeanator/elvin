using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Lvn.UI
{
    /// <summary>
    /// The engine's font pipeline: legacy <see cref="Font"/> references (theme
    /// fonts from Resources, downloaded .ttf files) are wrapped ONCE into
    /// TextCore SDF <see cref="FontAsset"/>s and applied via
    /// <c>unityFontDefinition</c> — the modern UITK text path (crisp under panel
    /// scaling, fallback-capable), replacing the legacy non-SDF
    /// <c>style.unityFont</c> route.
    ///
    /// Wrapped assets are DYNAMIC (glyphs rasterize on first use), so dialogue
    /// text must be pre-warmed at chapter load via <see cref="Prewarm"/> —
    /// otherwise the typewriter pays a rasterization hitch on every new glyph.
    /// </summary>
    public static partial class LvnFonts
    {


        // Script-coverage fallbacks built from fonts the OS ships (nothing added
        // to the build): a broad Latin+Cyrillic face per platform, then CJK.
        // Built ONE FONT PER FRAME in the background — creating six SDF assets
        // synchronously froze the first frame that touched any font. Colour
        // emoji are deliberately absent — bitmap emoji don't survive the SDF
        // pipeline; they come later via a sprite asset.
        private static List<FontAsset> _osFallbacks; // null until the builder finishes
        private static bool _osKicked;




        // ── ШРИФТ ТЕМЫ ──────────────────────────────────────────────────
        // Один вызов на корень — и всё дерево получает шрифт: unityFontDefinition
        // наследуется вниз. Иначе шрифт пришлось бы ставить в каждом экране, и
        // ровно один из них всегда оказывался бы забытым.
        private static Font _default;
        private static string _defaultPath;

        /// <summary>Шрифт действующей темы (загружается один раз на путь).</summary>
        public static Font Default
        {
            get
            {
                // ТЕМАТИЧЕСКИЙ путь, без подмены: выбор игрока накладывает
                // Apply. Подмени здесь — и «как в игре» возвращало бы не тему,
                // а прошлый выбор игрока.
                var path = LvnTheme.Current != null ? LvnTheme.Current.FontPath : null;
                if (string.IsNullOrEmpty(path)) return null;
                if (_default != null && _defaultPath == path) return _default;
                _defaultPath = path;
                _default = Resources.Load<Font>(path);
                return _default;
            }
        }

        /// <summary>Шрифт заголовков — тот же гарнитуры, но тяжёлый.</summary>
        /// <summary>
        /// ШРИФТ ДВИЖКА ИЗ КОРОБКИ — Onest (OFL, лежит в пакете): у него родная
        /// кириллица, и он не безликий. Системный Arial выдаёт «сделано на
        /// коленке» раньше, чем прочитан текст.
        ///
        /// <para>Путь стоял умолчанием в ДВУХ темах — сцены и оболочки, — и
        /// совпадал он по договорённости, а не по устройству. Разойдись они на
        /// одну правку, и диалог с меню поехали бы разными начертаниями:
        /// заметно сразу, а искать пришлось бы в двух файлах, где написано одно
        /// и то же.</para>
        /// </summary>
        public const string EngineFontPath = "Fonts/Onest-Regular";

        /// <summary>Заголовочное начертание того же семейства.</summary>
        public const string EngineDisplayPath = "Fonts/Onest-ExtraBold";

        // ── гарнитуры на выбор игрока ────────────────────────────────────────

        /// <summary>Гарнитура из каталога: чем набирать и как её назвать
        /// игроку.</summary>
        public readonly struct Family
        {
            public readonly string Id;       // ключ в настройках
            public readonly string Title;    // подпись в списке
            public readonly string Path;     // текст
            public readonly string Display;  // заголовки

            /// <summary>
            /// РАЗМЕР ГАРНИТУРЫ НЕ ЗАДАЁТСЯ ЧИСЛОМ — он измеряется.
            ///
            /// <para>Здесь стояли две подобранные глазом поправки: общий
            /// множитель кегля и сжатие шкалы. Подобранное глазом число живёт
            /// ровно до следующей гарнитуры и молча устаревает — «от руки
            /// огромен, а пиксель мал» это они и есть. Теперь величину буквы у
            /// шрифта СПРАШИВАЮТ (см. OpticalScale), и каталог описывает
            /// только то, что действительно про гарнитуру: кто она, как
            /// называется и где лежит.</para>
            /// </summary>
            public Family(string id, string title, string path, string display)
            {
                Id = id; Title = title; Path = path; Display = display;
            }
        }

        /// <summary>
        /// ПЯТЬ ГАРНИТУР, И КАЖДАЯ ЗАЧЕМ-ТО.
        ///
        /// <para>Набор не «побольше вариантов», а разные ответы на «чем читать
        /// длинный текст с телефона»: нейтральный гротеск, интерфейсный,
        /// русский по происхождению, книжный засечный и геометричный. Все — с
        /// родной кириллицей и по свободной лицензии (OFL, тексты в
        /// Third-Party-Notices.md): шрифт без кириллицы в русской новелле
        /// показывает не текст, а квадраты.</para>
        ///
        /// <para>Заголовочное начертание есть только у Onest — у остальных
        /// заголовки набираются тем же файлом: переменные шрифты Google несут
        /// все веса в одном файле, и отдельный «жирный» им не нужен.</para>
        /// </summary>
        public static readonly Family[] Families =
        {
            new Family("onest",   "Onest",      "Fonts/Onest-Regular", "Fonts/Onest-ExtraBold"),
            new Family("inter",   "Inter",      "Fonts/Inter",         "Fonts/Inter"),
            new Family("golos",   "Golos",      "Fonts/GolosText",     "Fonts/GolosText"),
            new Family("literata","Literata",   "Fonts/Literata",      "Fonts/Literata"),
            new Family("manrope", "Manrope",    "Fonts/Manrope",       "Fonts/Manrope"),
            // Характерные — их видно с первого слова. Ради этого они и есть:
            // настройка, которую нельзя проверить взглядом, ощущается сломанной.
            new Family("ruslan",  "Вязь",       "Fonts/RuslanDisplay", "Fonts/RuslanDisplay"),
            new Family("caveat",  "От руки",    "Fonts/Caveat",        "Fonts/Caveat"),
            new Family("pixel",   "Пиксель",    "Fonts/PressStart2P",  "Fonts/PressStart2P"),
            new Family("rubik",   "Плакат",     "Fonts/RubikMonoOne",  "Fonts/RubikMonoOne"),
        };

        /// <summary>Гарнитура по ключу настройки; неизвестный ключ и пустой —
        /// первая (шрифт движка из коробки).</summary>
        public static Family FamilyOf(string id)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (var f in Families)
                    if (f.Id == id) return f;
            return Families[0];
        }

        /// <summary>Что выбрал игрок. Пусто — гарнитура НОВЕЛЛЫ (тема), иначе
        /// выбор перекрывает её: подогнать шрифт под свои глаза важнее
        /// авторского вкуса, и это ровно та настройка, ради которой её
        /// просили.</summary>
        public static Family Chosen => FamilyOf(LvnPrefs.FontFamily);

        /// <summary>Выбрал ли игрок гарнитуру сам.</summary>
        public static bool PlayerPicked => !string.IsNullOrEmpty(LvnPrefs.FontFamily);

        /// <summary>
        /// НАЧЕРТАНИЕ ПО ВЫБРАННОЙ ТОЛЩИНЕ. Ползунок непрерывный, а начертаний
        /// у шрифта конечное число — поэтому здесь он превращается в ступени:
        /// обычное, полужирное (если у семейства оно есть), жирное.
        ///
        /// <para>Толщина — про читаемость, а не про вкус: тонкая гарнитура на
        /// светлом фоне и мелком кегле теряется, и это лечится весом, а не
        /// размером.</para>
        /// </summary>
        public static FontStyle WeightStyle
            => LvnPrefs.TextWeight >= 0.5f ? FontStyle.Bold : FontStyle.Normal;

        /// <summary>Начертание ИНТЕРФЕЙСА: своя ручка — меню читают мельком и
        /// по краю экрана, реплики вдумчиво и по центру.</summary>
        public static FontStyle UiWeightStyle
            => LvnPrefs.UiWeight >= 0.5f ? FontStyle.Bold : FontStyle.Normal;

        /// <summary>
        /// ТОЛЩИНА БЕЗ ЖИРНОГО ФАЙЛА. Отдельных начертаний у наших гарнитур
        /// нет — они переменные, и Unity берёт из них один инстанс. Но текст
        /// рисуется SDF-материалом, а у него есть раздутие контура
        /// (<c>_FaceDilate</c>): им глиф утолщается ПЛАВНО, без второго файла и
        /// без фальшивого жира, которым UITK эмулирует Bold.
        ///
        /// <para>Материал общий для всех, кто набран этой гарнитурой, поэтому
        /// толщина одна на игру — что и правильно: две разные толщины одного
        /// шрифта на одном экране читаются как ошибка, а не как настройка.</para>
        ///
        /// <para>Предел скромный (0…0,12): дальше буквы слипаются на мелком
        /// кегле, и «жирнее» превращается в «грязнее».</para>
        ///
        /// <para>ПОКА НЕ РАБОТАЕТ и в настройках скрыто: материал текста в UI
        /// Toolkit — не TMP-овский, свойства раздутия у него нет, и вызов молча
        /// ничего не меняет. Оставлено намеренно: у Onest весовые файлы есть, и
        /// путь через <see cref="WeightedPath"/> рабочий — не хватает таких
        /// файлов у остальных гарнитур.</para>
        /// </summary>
        public static void ApplyWeight()
        {
            float dilate = Mathf.Clamp01(LvnPrefs.TextWeight) * 0.12f;
            foreach (var kv in _wrapped)
            {
                var fa = kv.Value;
                if (fa == null || fa.material == null) continue;
                try { fa.material.SetFloat(FaceDilate, dilate); }
                catch { /* чужой шейдер без этого свойства — толщина просто не изменится */ }
            }
            Changed?.Invoke();
        }

        private static readonly int FaceDilate = Shader.PropertyToID("_FaceDilate");

        /// <summary>Путь начертания под текущую толщину: у семейства с
        /// промежуточным весом (Onest SemiBold) середина ползунка берёт его, а
        /// не прыгает сразу в жирный.</summary>
        public static string WeightedPath(string basePath)
        {
            float w = LvnPrefs.TextWeight;
            if (w < 0.34f || string.IsNullOrEmpty(basePath)) return basePath;
            if (basePath.EndsWith("Onest-Regular"))
                return w < 0.67f ? "Fonts/Onest-SemiBold" : "Fonts/Onest-ExtraBold";
            return basePath;   // у остальных семейств вес добирается стилем
        }






        private static readonly System.Collections.Generic.Dictionary<string, float> _optical
            = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>Прежние имена умолчаний — их читают темы при сборке.</summary>
        public const string DefaultPath = EngineFontPath;
        public const string DefaultDisplayPath = EngineDisplayPath;

        private static Font _display;
        private static string _displayPath;
        public static Font Display
        {
            get
            {
                var path = LvnTheme.Current != null ? LvnTheme.Current.FontDisplayPath : null;
                if (string.IsNullOrEmpty(path)) return null;
                if (_display != null && _displayPath == path) return _display;
                _displayPath = path;
                _display = Resources.Load<Font>(path);
                return _display;
            }
        }

        // ── ЖИВАЯ СМЕНА ГАРНИТУРЫ ────────────────────────────────────────────
        //
        // Шрифт ставится элементам поштучно и в момент их сборки: диалог, выборы,
        // ввод имени, корни слоёв. Выбери игрок другую гарнитуру — и ничего не
        // произойдёт до пересборки экрана, то есть настройка выглядит сломанной
        // («шрифты не меняются», Илья 28.08). Поэтому дом ПОМНИТ, кому он
        // применял шрифт, и переставляет его всем разом.
        //
        // Слабые ссылки: экраны сносятся и пересобираются десятками за сессию, и
        // список, держащий их живыми, был бы утечкой ровно того размера, что и
        // сама игра.
        private static readonly List<(System.WeakReference<VisualElement> el, Font asked)> _applied
            = new List<(System.WeakReference<VisualElement>, Font)>();
        private static bool _hooked;

        private static void Remember(VisualElement el, Font asked)
        {
            if (!_hooked)
            {
                _hooked = true;
                LvnPrefs.Changed += OnPrefsChanged;
            }
            for (int i = _applied.Count - 1; i >= 0; i--)
            {
                if (!_applied[i].el.TryGetTarget(out var live)) { _applied.RemoveAt(i); continue; }
                if (ReferenceEquals(live, el)) { _applied[i] = (_applied[i].el, asked); return; }
            }
            _applied.Add((new System.WeakReference<VisualElement>(el), asked));
        }

        private static string _lastFamily = "";

        private static float _lastWeight = -1f;

        private static void OnPrefsChanged()
        {
            if (!Mathf.Approximately(_lastWeight, LvnPrefs.TextWeight))
            {
                _lastWeight = LvnPrefs.TextWeight;
                ApplyWeight();
            }
            var now = LvnPrefs.FontFamily ?? "";
            if (now == _lastFamily) return;   // сменилось что-то другое — не тревожим текст
            _lastFamily = now;
            Refresh();
        }

        /// <summary>Переставить шрифт всем, кому его ставили. Зовётся сама при
        /// смене гарнитуры; хосту нужна, если он менял настройку в обход.</summary>
        public static void Refresh()
        {
            _default = null; _defaultPath = null;      // пути темы теперь ведут в другую гарнитуру
            _display = null; _displayPath = null;
            for (int i = _applied.Count - 1; i >= 0; i--)
            {
                if (!_applied[i].el.TryGetTarget(out var el) || el.panel == null && el.parent == null)
                { _applied.RemoveAt(i); continue; }
                var font = Override(_applied[i].asked);
                if (font == null) continue;
                var fa = From(font);
                if (fa != null) el.style.unityFontDefinition = new StyleFontDefinition(fa);
                else el.style.unityFont = font;
            }
            // Прогрев новой гарнитуры: глифы у динамического шрифта
            // растеризуются при первом показе, и без прогрева смена читается
            // как рывок ровно в тот момент, когда игрок смотрит на текст.
            var chosen = PlayerPicked ? Resources.Load<Font>(Chosen.Path) : Default;
            if (chosen != null) Prewarm(chosen, WarmAlphabet);
            Changed?.Invoke();
        }

        // Что греть: обе азбуки, цифры и знаки, которыми набран интерфейс.
        // Кириллица здесь не «на всякий случай» — на ней написана вся игра.
        private const string WarmAlphabet =
            "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            " .,!?:;—-–«»\"'()[]…%+/№";

        /// <summary>Гарнитура сменилась — экраны, считающие кегль или ширину от
        /// шрифта, пересчитываются.</summary>
        public static event System.Action Changed;

        // Выбор игрока перекрывает и тему новеллы, и шрифт из контента: он
        // сделан ради читаемости, а не ради вкуса, и «почти везде» здесь
        // означает «не работает».
        private static Font Override(Font asked)
        {
            if (!PlayerPicked) return asked;
            var path = WeightedPath(Chosen.Path);
            if (string.IsNullOrEmpty(path)) return asked;
            if (_chosenCache != null && _chosenPath == path) return _chosenCache;
            _chosenPath = path;
            _chosenCache = Resources.Load<Font>(path);
            if (_chosenCache == null)
                Debug.LogWarning($"[lvn-fonts] гарнитура «{Chosen.Title}» не найдена по пути {path} — " +
                                 "остаётся прежняя");
            return _chosenCache ?? asked;
        }

        private static Font _chosenCache;
        private static string _chosenPath;

        /// <summary>Поставить шрифт темы на корень слоя.</summary>
        public static void ApplyDefault(VisualElement root) => Apply(root, Default);

        /// <summary>Apply a font to an element the modern way (SDF FontAsset via
        /// unityFontDefinition), falling back to the legacy assignment only when
        /// the wrap failed. Null font = no-op (theme/panel default applies).</summary>
        public static void Apply(VisualElement el, Font font)
        {
            if (el == null) return;
            Remember(el, font);        // чтобы пережить смену гарнитуры на лету
            font = Override(font);     // выбор игрока сильнее любого другого шрифта
            if (font == null) return;
            var fa = From(font);
            if (fa != null) el.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromSDFFont(fa));
            else el.style.unityFont = new StyleFont(font);
        }


    }
}
