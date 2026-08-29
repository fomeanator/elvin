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
    public static class LvnFonts
    {
        private static readonly Dictionary<Font, FontAsset> _wrapped = new Dictionary<Font, FontAsset>();
        private static readonly Dictionary<string, Font> _fromFile = new Dictionary<string, Font>();

        /// <summary>The SDF FontAsset for a legacy Font (cached; null when the
        /// wrap fails — callers then fall back to the legacy path). Every wrapped
        /// asset gets the shared OS fallback chain, so a theme font that lacks
        /// Cyrillic/CJK still renders those runs instead of tofu.</summary>
        public static FontAsset From(Font font)
        {
            if (font == null) return null;
            if (_wrapped.TryGetValue(font, out var fa)) return fa;
            try { fa = FontAsset.CreateFontAsset(font); }
            catch { fa = null; }
            if (fa != null && _osFallbacks != null)
                try { fa.fallbackFontAssetTable = _osFallbacks; } catch { }   // шрифт не обернулся в SDF — ниже запасной путь
            if (fa != null && fa.material != null)
                try { fa.material.SetFloat(FaceDilate, Mathf.Clamp01(LvnPrefs.TextWeight) * 0.12f); }
                catch { /* см. ApplyWeight: чужой шейдер молча остаётся как есть */ }
            _wrapped[font] = fa; // cache failures too — don't retry every label
            KickOsFallbacks(); // built in the background, attached when ready
            return fa;
        }

        // Script-coverage fallbacks built from fonts the OS ships (nothing added
        // to the build): a broad Latin+Cyrillic face per platform, then CJK.
        // Built ONE FONT PER FRAME in the background — creating six SDF assets
        // synchronously froze the first frame that touched any font. Colour
        // emoji are deliberately absent — bitmap emoji don't survive the SDF
        // pipeline; they come later via a sprite asset.
        private static List<FontAsset> _osFallbacks; // null until the builder finishes
        private static bool _osKicked;

        private static void KickOsFallbacks()
        {
            if (_osKicked) return;
            _osKicked = true;
            LvnAsync.Fire(BuildOsFallbacksAsync(), "BuildOsFallbacks");
        }

        private static async System.Threading.Tasks.Task BuildOsFallbacksAsync()
        {
            var list = new List<FontAsset>();

            // СПРАШИВАЕМ У СИСТЕМЫ ТОЛЬКО ТО, ЧТО У НЕЁ ЕСТЬ. Раньше запасные
            // гарнитуры перебирались вслепую, а Unity на отсутствующую отвечает
            // не null, а объектом-пустышкой — и печатает ДВЕ ошибки в лог:
            // «Unable to find a font file [Roboto]» и «Unable to load font
            // face». На маке таких три из шести, то есть шесть красных строк на
            // каждом запуске. Ошибка, которая ничего не значит, дороже
            // молчания: она приучает не читать лог, и настоящая теряется среди
            // неё (живой лог Ильи, 28.08).
            var installed = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Font.GetOSInstalledFontNames() ?? System.Array.Empty<string>())
                    if (!string.IsNullOrEmpty(f))
                    {
                        installed.Add(f);
                        installed.Add(f.Replace(" ", ""));   // «Helvetica Neue» ↔ «HelveticaNeue»
                    }
            }
            catch { /* платформа не отдаёт список — переберём вслепую, как раньше */ }

            foreach (var name in new[]
                     {
                         "Roboto", "Helvetica Neue", "Arial",          // Latin + Cyrillic
                         "PingFang SC", "Noto Sans CJK SC", "Yu Gothic" // CJK (when present)
                     })
            {
                if (installed.Count > 0
                    && !installed.Contains(name) && !installed.Contains(name.Replace(" ", "")))
                    continue;   // этой гарнитуры на системе нет — не тревожим TMP
                await System.Threading.Tasks.Task.Yield(); // one asset per frame — no spike
                try
                {
                    var os = Font.CreateDynamicFontFromOSFont(name, 90);
                    if (os == null) continue;
                    var fa = FontAsset.CreateFontAsset(os);
                    if (fa != null) list.Add(fa);
                }
                catch { /* missing on this OS — next candidate */ }
            }
            _osFallbacks = list;
            // Late-attach to every font wrapped before the chain was ready.
            foreach (var kv in _wrapped)
                if (kv.Value != null)
                    try { kv.Value.fallbackFontAssetTable = list; } catch { }   // файл шрифта не читается — останется гарнитура панели
        }

        /// <summary>A Font loaded from a file on disk (downloaded/StreamingAssets
        /// locale packs) — never Resources. Cached per path.</summary>
        public static Font FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_fromFile.TryGetValue(path, out var f)) return f;
            try { f = new Font(path); }
            catch { f = null; }
            _fromFile[path] = f;
            return f;
        }

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

        /// <summary>Поправка кегля под выбранную гарнитуру. Единица, пока игрок
        /// не выбирал: авторский кегль подобран под авторский шрифт.</summary>
        public static float SizeFactor => PlayerPicked ? OpticalScale(Chosen) : 1f;

        /// <summary>
        /// Кегль с поправкой на гарнитуру: спрашивают ЗДЕСЬ, а не умножают у
        /// себя — иначе поправка доедет до половины экранов.
        ///
        /// <para>Поправок две, и делают они разное. Множитель двигает ВСЮ
        /// шкалу (рукописная мельче гротеска при том же числе). Сжатие тянет
        /// ступени к базовому кеглю: у характерных гарнитур крупное вылезает
        /// сильнее, чем задумано, и «Заголовок» рядом с подписью выглядит
        /// плакатом. Степень сохраняет порядок ступеней — мелкое остаётся
        /// мельче крупного, просто разница перестаёт кричать.</para>
        /// </summary>
        public static int Size(float baseSize)
        {
            if (!PlayerPicked || baseSize <= 0f)
                return UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(baseSize));
            float value = baseSize * OpticalScale(Chosen);
            // МЯГКИЕ ГРАНИЦЫ. Измерение честное, но шрифт может прийти со
            // сломанными метриками (или вовсе не собраться), и тогда одна
            // строка каталога сломала бы вёрстку всех экранов разом.
            value = UnityEngine.Mathf.Clamp(value, baseSize * 0.5f, baseSize * 2.5f);
            return UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(value));
        }

        /// <summary>
        /// ОДИН КЕГЛЬ — ОДИН ВИДИМЫЙ РАЗМЕР, у любой гарнитуры.
        ///
        /// <para>Кегль (те самые «30») не описывает величину букв: это высота
        /// площадки, на которой шрифт нарисован, а сколько он на ней занимает —
        /// дело рисовальщика. У рукописной строчные вдвое ниже площадки (место
        /// съедают петли и росчерки), у пиксельной прописные почти во всю её
        /// высоту. Один и тот же «30» даёт у них разницу вдвое — что и было
        /// видно: «от руки огромен, а пиксель мал».</para>
        ///
        /// <para>Раньше поправка подбиралась глазом и стояла числом в каталоге.
        /// Числа устаревают молча: добавили гарнитуру — подобрали заново,
        /// ошиблись — узнали по скриншоту. Теперь размер СЧИТАЕТСЯ по самому
        /// шрифту, из его метрик, и новая гарнитура встаёт правильно без
        /// подбора.</para>
        ///
        /// <para>Мера — СРЕДНЕЕ ИЗ СТРОЧНОЙ И ПРОПИСНОЙ, и это не компромисс
        /// ради красоты. По одной строчной мерить нельзя: у рукописной они
        /// вдвое ниже эталонных (31.9 против 47.4), а прописные, наоборот,
        /// ВЫШЕ (70.6 против 63.6) — растянув её по строчным, мы делаем
        /// заголовки в полтора раза крупнее эталонных, и получается ровно
        /// «от руки огромен». Замер девяти гарнитур: по строчной разброс
        /// кегля выходит двукратный (22…45 при авторских 30), по среднему —
        /// 25…32.</para>
        ///
        /// <para>Буквы берутся НАРИСОВАННЫЕ, а не заявленные в метриках. У
        /// пиксельной строчных нет вовсе, и линия строчных проставлена
        /// формально — 68 при фактической высоте буквы 56.3; поверив метрике,
        /// мы уменьшали её сильнее, чем нужно.</para>
        /// </summary>
        private static float OpticalScale(Family fam)
        {
            if (string.IsNullOrEmpty(fam.Id)) return 1f;   // Family — структура, «пустая» узнаётся по имени
            if (_optical.TryGetValue(fam.Id, out var cached)) return cached;

            float mine = LetterHeight(fam.Path);
            float reference = Families.Length > 0 ? LetterHeight(Families[0].Path) : 0f;
            float scale = mine > 0.0001f && reference > 0.0001f ? reference / mine : 1f;
            _optical[fam.Id] = scale;
            LvnLog.Trace($"[lvn-fonts] {fam.Id}: буква {mine:0.###} против эталонной {reference:0.###} → кегль ×{scale:0.##}");
            return scale;
        }

        // Средняя высота нарисованной буквы в долях кегля. Ноль означает
        // «измерить не вышло» — вызывающий тогда оставляет кегль как есть.
        private static float LetterHeight(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath)) return 0f;
            var font = Resources.Load<Font>(resourcePath);
            if (font == null) return 0f;
            var fa = From(font);
            if (fa == null || fa.faceInfo.pointSize <= 0f) return 0f;

            float low = GlyphHeight(fa, 'x', 'о');    // строчная: латиница, иначе кириллица
            float high = GlyphHeight(fa, 'H', 'О');   // прописная
            if (low <= 0.0001f) low = high;           // шрифт только с прописными
            if (high <= 0.0001f) high = low;
            if (low <= 0.0001f) return 0f;
            return (low + high) * 0.5f / fa.faceInfo.pointSize;
        }

        // Высота нарисованного глифа. Динамический SDF рисует символ по
        // требованию — поэтому сначала просим его добавить.
        private static float GlyphHeight(FontAsset fa, params char[] candidates)
        {
            foreach (var c in candidates)
            {
                try { fa.TryAddCharacters(c.ToString()); } catch { }
                if (fa.characterLookupTable != null
                    && fa.characterLookupTable.TryGetValue(c, out var ch)
                    && ch?.glyph != null && ch.glyph.metrics.height > 0.0001f)
                    return ch.glyph.metrics.height;
            }
            return 0f;
        }

        private static readonly System.Collections.Generic.Dictionary<string, float> _optical
            = new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>Путь текстового шрифта с учётом выбора игрока: тема
        /// спрашивает ЗДЕСЬ, а не читает своё поле напрямую.</summary>
        public static string PathFor(string themePath)
            => PlayerPicked ? Chosen.Path : (string.IsNullOrEmpty(themePath) ? EngineFontPath : themePath);

        /// <summary>То же для заголовков.</summary>
        public static string DisplayPathFor(string themePath)
            => PlayerPicked ? Chosen.Display : (string.IsNullOrEmpty(themePath) ? EngineDisplayPath : themePath);

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

        /// <summary>Rasterize every distinct character of <paramref name="text"/>
        /// into the font's atlas — SPREAD over frames (a whole chapter's corpus
        /// in one call froze the entry for hundreds of ms). Fire-and-forget: the
        /// first line may still rasterize a few glyphs on-reveal, but never the
        /// whole alphabet at once. Missing glyphs cascade into the same fallback
        /// assets the renderer will pick at draw time.</summary>
        public static void Prewarm(Font font, string text) => LvnAsync.Fire(PrewarmSpreadAsync(font, text), "PrewarmSpread");

        private static async System.Threading.Tasks.Task PrewarmSpreadAsync(Font font, string text, int charsPerFrame = 48)
        {
            if (font == null || string.IsNullOrEmpty(text)) return;
            var fa = From(font);
            if (fa == null) return;
            var distinct = new HashSet<char>();
            var sb = new StringBuilder(256);
            foreach (var c in text)
                if (!char.IsControl(c) && distinct.Add(c)) sb.Append(c);
            for (int i = 0; i < sb.Length; i += charsPerFrame)
            {
                var chunk = sb.ToString(i, System.Math.Min(charsPerFrame, sb.Length - i));
                string missing;
                try { fa.TryAddCharacters(chunk, out missing); }
                catch { return; /* atlas full / dynamic-OS font — render-time fallback covers it */ }
                if (!string.IsNullOrEmpty(missing) && _osFallbacks != null)
                    foreach (var fb in _osFallbacks)
                    {
                        try { fb.TryAddCharacters(missing, out missing); }
                        catch { break; }
                        if (string.IsNullOrEmpty(missing)) break;
                    }
                await System.Threading.Tasks.Task.Yield(); // one chunk per frame
            }
        }
    }
}
