using System;

namespace Lvn.Content
{
    /// <summary>
    /// The visual/asset class of a content URL, inferred from its path. The
    /// single place that decides "what KIND of thing is this URL" so every
    /// download phase agrees (boot prefetch, menu refresh, chapter entry, in-game
    /// look-ahead) instead of each re-deriving it from path substrings inline.
    /// </summary>
    public enum AssetClass
    {
        Ui,         // shared interface art (dialogue frame, badges, icons)
        ChapterBg,  // a chapter's loading-screen background
        Cover,      // a title cover (menu carousel)
        Script,     // a .lvn chapter script
        Actor,      // character art
        Audio,      // music / sfx
        SceneBg,    // in-chapter scene background
        Other,
    }

    /// <summary>
    /// Pure, deterministic download policy — classifies content URLs and answers
    /// the cross-cutting questions the download phases ask:
    /// <list type="bullet">
    ///   <item>what class is this URL?</item>
    ///   <item>should it be decoded into the in-memory sprite cache (warm), or is
    ///   living on disk enough?</item>
    ///   <item>is it wanted during boot prefetch?</item>
    /// </list>
    /// No UnityEngine, no I/O — every rule here is unit-testable, so "what loads
    /// when" is a calculable contract rather than scattered path checks. The
    /// caller supplies the actual URLs and performs the side effects; this only
    /// judges. Path conventions (<c>/ui/</c>, <c>/loading/</c>, <c>/cover</c>,
    /// <c>/actors/</c>, <c>/bg/</c>) are sensible defaults — a host with a
    /// different layout can classify by server-supplied <see cref="LvnAssetMeta"/>
    /// instead.
    ///
    /// Обрезка строки запроса живёт в <see cref="LvnUrl"/>: это ключ кэша, и
    /// своя копия правила здесь однажды разошлась бы с планировщиком.
    /// </summary>
    public static class DownloadPolicy
    {
        public static bool IsImage(string url)
        {
            var u = LvnUrl.Bare(url);
            return u.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAudio(string url)
        {
            var u = LvnUrl.Bare(url);
            return u.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsScript(string url) =>
            LvnUrl.Bare(url).EndsWith(".lvn", StringComparison.OrdinalIgnoreCase);

        /// <summary>The prefetch kind string ("sprite" | "audio" | "bin").</summary>
        public static string Kind(string url) =>
            IsImage(url) ? "sprite" : IsAudio(url) ? "audio" : "bin";

        /// <summary>The "@2k" downscale-variant url for large story art, or null
        /// when the url must load as-is (pixel art, UI skins, already a variant,
        /// non-raster extensions). Every phase that fetches sprites (display,
        /// preload, chapter scheduler) must agree on this mapping so they all
        /// warm/read the SAME cached file.</summary>
        /// <summary>Бокс показа: "@2k" (высокое) или "@1k" (экономия трафика —
        /// ручка «Качество арта»). Хост синхронизирует с настройкой игрока.</summary>
        public static string PreferredSuffix = "@2k";

        /// <summary>
        /// СКОЛЬКО ВЕСИТ ФАЙЛ, О КОТОРОМ МЫ НЕ ЗНАЕМ — скромная оценка для
        /// полосы прогресса, когда у ассета нет меты с размером.
        ///
        /// <para>Оценка стояла семью литералами в двух файлах хоста: подсчёт
        /// «сколько осталось скачать», сборка очереди, прогрев бут-набора,
        /// перекачка в новом качестве. Правило одно, записей семь — и разойтись
        /// им ничего не мешало: полосы в разных экранах начали бы считать
        /// по-разному, а понять, какая права, было бы не по чему.</para>
        ///
        /// <para>Скромная нарочно: занизить оценку значит показать прогресс,
        /// который к концу «замедляется», завысить — который прыгает к
        /// завершению. Первое игрок прощает, второе читается как обман.</para>
        /// </summary>
        public const long UnknownSizeBytes = 64 << 10;

        /// <summary>ИСХОДНЫЙ url без суффикса варианта: «bg/x@2k.jpg» → «bg/x.jpg».
        ///
        /// <para>Один и тот же файл живёт под несколькими именами (полный,
        /// @2k, @1440, @1k, @mini), и «а это тот же самый арт?» спрашивают
        /// сид, дисковый кэш и стриминг. Перечень суффиксов, размазанный по
        /// вызовам, однажды разъедется с тем, что реально кодирует сервер.</para></summary>
        public static string StripVariant(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            foreach (var s in Variants) url = url.Replace(s, "");
            return url;
        }

        /// <summary>Все суффиксы вариантов, которые встречаются в контенте.</summary>
        public static readonly string[] Variants = { "@2k", "@1440", "@1k", "@mini" };

        /// <summary>ИМЯ КРУПНОГО ВАРИАНТА — «@2k». Одно слово, но зашито оно
        /// было в четырёх местах: спайн лепил суффикс своей строкой, разбор
        /// имени файла на диске сравнивал с литералом, уборка чужих боксов
        /// перечисляла варианты списком. Стоит серверу переименовать бокс — и
        /// расходятся ровно те места, которые никто не свяжет.</summary>
        public const string DisplayVariant = "@2k";

        /// <summary>
        /// ИСХОДНАЯ КАРТИНКА — та, из которой контент делает другие.
        ///
        /// <para>Разложить адрес на «имя без расширения» и «расширение», а
        /// заодно ответить, картинка ли это вообще: у чужого расширения (звук,
        /// сценарий, бандл, уже готовый .ktx2) вариантов и перекодировок не
        /// бывает.</para>
        ///
        /// <para>Проверка стояла ЧЕТЫРЕЖДЫ дословно — у варианта качества, у
        /// уменьшенного показа, у ASTC и у KTX2, — а список расширений в ней
        /// один. Добавить контенту .webp значило вспомнить все четыре; забыть
        /// одно — получить перекодировку, которая молча ничего не делает для
        /// половины арта.</para>
        /// </summary>
        private static bool HasFolder(string url, string folder)
            => url.IndexOf(folder, System.StringComparison.OrdinalIgnoreCase) >= 0;

        /// <remarks><paramref name="ext"/> — ВСЁ ОТ ПОСЛЕДНЕЙ ТОЧКИ ДО КОНЦА,
        /// как в адресе: родной регистр («.PNG» — приведённое к нижнему имя на
        /// Linux-проде не найдётся) и хвост запроса, если он есть. Тогда
        /// <c>stem + "@2k" + ext</c> даёт «bg/x@2k.jpg?v=3», а не теряет
        /// кэш-бастер посреди адреса.</remarks>
        public static bool SplitSourceImage(string url, out string stem, out string ext)
        {
            stem = null; ext = null;
            if (string.IsNullOrEmpty(url)) return false;
            // Расширение спрашиваем у ДОМА АДРЕСА: он один знает, что «?v=3» к
            // имени файла не относится. Считать его здесь заново значило бы
            // объявить «bg/x.png?v=3» не картинкой — и молча лишить её ступеней
            // качества и силуэт-заготовки.
            var e = LvnUrl.Extension(url);
            if (e != "png" && e != "jpg" && e != "jpeg") return false;
            int dot = LvnUrl.Bare(url).LastIndexOf('.');
            if (dot < 0) return false;
            stem = url.Substring(0, dot);
            ext = url.Substring(dot);
            return true;
        }

        /// <summary>Навесить конкретный вариант на url (те же исключения, что у
        /// <see cref="DownscaleVariant"/>): «bg/x.jpg» + «@1k» → «bg/x@1k.jpg».</summary>
        public static string WithVariant(string url, string variant)
        {
            if (string.IsNullOrEmpty(variant)) return null;
            return SplitSourceImage(url, out var stem, out var ext) ? stem + variant + ext : null;
        }

        /// <summary>Варианты ПОКАЗА (без «@mini»): их держит на диске центр
        /// загрузок, между ними переключается настройка качества.</summary>
        public static readonly string[] QualityVariants = { "@2k", "@1440", "@1k" };

        public static string DownscaleVariant(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.Contains("/pixel/") || url.Contains("/ui/") || url.Contains("@")) return null;
            // /spine/ pages are here because the SPINE display path also renders
            // from @2k (VnStage.Spine LoadSpineImageAsync) — warming the original
            // would download+decode a full-size page the renderer never samples.
            // Папки сверяются БЕЗ УЧЁТА РЕГИСТРА — как и расширение. Иначе
            // «/Art/Hero.PNG» вариантов не получал: качался и декодировался
            // полноразмерный арт, а ручка «Качество арта» и крошка-заготовка
            // для такого контента молча переставали работать.
            if (!(HasFolder(url, "/bg/") || HasFolder(url, "/art/")
                  || HasFolder(url, "/sprites/") || HasFolder(url, "/spine/"))) return null;
            return SplitSourceImage(url, out var stem, out var ext) ? stem + PreferredSuffix + ext : null;
        }

        /// <summary>
        /// КАКОЙ АДРЕС СКАЧАЕТСЯ НА САМОМ ДЕЛЕ для ассета этого вида.
        ///
        /// <para>Показ берёт крупный арт уменьшённым вариантом (@2k), а фон и
        /// звук — как есть. Правило простое, и потому его писали по месту:
        /// <c>kind == "sprite" ? (DownscaleVariant(url) ?? url) : url</c> стояло
        /// ПЯТЬЮ копиями в четырёх фазах загрузки — прогрев бута, обновление
        /// меню, вход в главу, «скачать всю игру».</para>
        ///
        /// <para>Копии опасны не тем, что их много, а тем, что расходятся
        /// молча: фаза, забывшая уменьшение, качает полноразмерный файл, а
        /// показ потом просит другой адрес — и один и тот же арт лежит на диске
        /// дважды, при этом «уже скачано» не срабатывает.</para>
        /// </summary>
        public static string Effective(string kind, string url)
            => kind == "sprite" ? (DownscaleVariant(url) ?? url) : url;

        /// <summary>Микровариант для «силуэта-проявления»: крошечная (@mini,
        /// бокс 256) версия того же арта — актёр на медленной сети входит
        /// вовремя тёмной заготовкой, полный арт проявляет его следом. Null —
        /// у url нет варианта (те же исключения, что у DownscaleVariant).</summary>
        public static string MiniVariant(string url)
        {
            var v = DownscaleVariant(url);
            v = v?.Replace(PreferredSuffix, "@mini");
            // МИНИ — ВСЕГДА PNG: при ktx2-тракте вариант наследовал «.ktx2»,
            // которого сервер для крошек не кодирует — витрина гардероба и
            // силуэт-заготовки ловили сплошные 404 (живой скрин «одни
            // вешалки»), а промахи ещё и валили skip-streak ktx2-тракта.
            if (v != null && v.EndsWith(".ktx2")) v = v.Substring(0, v.Length - 5) + ".png";
            return v;
        }

        /// <summary>Classify by path segment. Order matters: script and audio win
        /// over the image buckets; among images, the path folder decides.</summary>
        public static AssetClass Classify(string url)
        {
            if (string.IsNullOrEmpty(url)) return AssetClass.Other;
            var u = LvnUrl.Bare(url).ToLowerInvariant();
            if (IsScript(u)) return AssetClass.Script;
            if (IsAudio(u))  return AssetClass.Audio;
            // Loading backgrounds live under /loading/ — check BEFORE /ui/.
            if (u.Contains("/loading/")) return AssetClass.ChapterBg;
            if (u.Contains("/ui/"))      return AssetClass.Ui;
            if (u.Contains("/cover"))    return AssetClass.Cover;
            if (u.Contains("/actors/") || u.Contains("/actor/")) return AssetClass.Actor;
            if (u.Contains("/bg/"))      return AssetClass.SceneBg;
            return AssetClass.Other;
        }

        /// <summary>Should this URL be decoded into the in-memory sprite cache up
        /// front (so a view can paint it on the first frame), or is on-disk
        /// enough? Warm the art the player sees immediately: shared UI, chapter
        /// loading backgrounds, and covers (the carousel is the first screen).
        /// Scene backgrounds, actors and audio stay disk-only — chapter-scoped,
        /// loaded when their command needs them.</summary>
        public static bool WarmToMemory(AssetClass cls) =>
            cls == AssetClass.Ui || cls == AssetClass.ChapterBg || cls == AssetClass.Cover;

        public static bool WarmToMemory(string url) => WarmToMemory(Classify(url));

        /// <summary>Is this URL part of the boot prefetch set — the art the player
        /// sees immediately at/after launch (UI chrome, menu covers, chapter
        /// loading backgrounds)? Scene backgrounds, actors and audio are
        /// chapter-scoped and fetched by the chapter scheduler, not at boot.</summary>
        public static bool NeededAtBoot(AssetClass cls) =>
            cls == AssetClass.Ui || cls == AssetClass.Cover || cls == AssetClass.ChapterBg;

        public static bool NeededAtBoot(string url) => NeededAtBoot(Classify(url));

        /// <summary>
        /// ЧТО РИСУЕТ ПЕРВЫЙ КАДР — и потому ЖДЁТСЯ на запуске.
        ///
        /// <para>Набор бута («что игрок увидит вскоре после запуска») и набор
        /// первого кадра («без чего нельзя показать окно») — РАЗНЫЕ вопросы, а
        /// отвечал на них один список: запуск скачивал обложки всех новелл и
        /// фоны загрузки всех глав, и приложение оживало через сотню мегабайт.
        /// Витрина рисует недостающие обложки заглушками и подставляет
        /// настоящие по мере приезда — значит первому кадру нужен только
        /// интерфейсный арт и полотно витрины, остальное догоняет фоном.</para>
        ///
        /// <para>Полотно приходит отдельным доводом: его адрес знает манифест
        /// (<c>ui.browse.canvas</c>), а по имени файла оно не отличимо от любой
        /// другой картинки.</para>
        /// </summary>
        public static bool NeededForFirstFrame(string url, string browseCanvas = null)
            => Classify(url) == AssetClass.Ui
               || (!string.IsNullOrEmpty(browseCanvas) && url == browseCanvas);
    }
}
