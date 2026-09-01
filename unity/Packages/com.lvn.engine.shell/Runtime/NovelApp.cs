using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The drop-in app bootstrap — the whole Liminal-style flow in one component:
    /// fetch the manifest from a server, boot-prefetch its assets, raise the
    /// <see cref="NovelShell"/> (boot → carousel → name → loading → title), and on
    /// Play stream the chosen chapter's <c>.lvn</c> and run it through a wired
    /// <see cref="VnStage"/>, updating the HUD, then loop back to the carousel.
    ///
    /// <para>Scene setup: one GameObject with this component (set
    /// <see cref="ServerUrl"/> + <see cref="ShellTheme"/>) and a second GameObject
    /// with a <see cref="VnStage"/> (its own UIDocument, a lower panel
    /// <c>sortingOrder</c> than the shell) assigned to <see cref="Stage"/>.</para>
    /// </summary>
    public sealed partial class NovelApp : MonoBehaviour
    {
        /// <summary>Shell lifecycle for the embedding game: a chapter is about
        /// to play (analytics, music ducking, achievements). Args: title, chapter.</summary>
        public event System.Action<LvnTitle, LvnChapter> ChapterStarted;

        /// <summary>A chapter finished END-TO-END (not an exit-to-menu).</summary>
        public event System.Action<LvnTitle, LvnChapter> ChapterFinished;

        [Tooltip("Content origin — the LVN server (manifest + scripts + assets).")]
        public string ServerUrl = "http://127.0.0.1:8000";

        /// <summary>Baked-in alternate servers offered by the boot server-select
        /// screen (display name, base URL up to but not including /api) — e.g. a
        /// partner's self-hosted mirror. Set from the product's Boot.cs, same as
        /// <see cref="ServerUrl"/>. The player can always type ANY custom URL
        /// regardless of this list; empty just means no presets besides "default".</summary>
        public (string Name, string Url)[] KnownServers = Array.Empty<(string, string)>();

        [Tooltip("Offline build: load the novel from content bundled in StreamingAssets " +
                 "instead of a server. The exporter writes the manifest, scripts and assets " +
                 "under StreamingAssets/<BundleSubdir>, mirroring the server's URL paths.")]
        public bool OfflineBundled = false;

        [Tooltip("Subfolder under StreamingAssets that holds the bundled content (offline builds).")]
        public string BundleSubdir = "lvn";

        [Tooltip("The VnStage that renders chapters. Its panel sortingOrder should be below the shell's (30).")]
        public VnStage Stage;

        [Tooltip("Language code for localized chapters. When set, each chapter loads " +
                 "its sidecar string catalog <script>.<locale>.json; lines with a " +
                 "text_id resolve through it. Empty = chapters use their inline text.")]
        public string Locale = "";

        /// <summary>
        /// ДЕЙСТВУЮЩИЙ ЯЗЫК: выбор игрока перекрывает умолчание сборки
        /// (<see cref="Locale"/> из инспектора).
        ///
        /// <para>Ответ на «какой сейчас язык» жил в ДВУХ переменных: настройка
        /// игрока и поле хоста, которое ей вручную присваивали — при загрузке
        /// манифеста и на каждое событие настроек. Ручная синхронизация двух
        /// хранилищ одного смысла держится ровно до первого пути, где
        /// присвоение забыли; читателю же нужен один ответ, а не два
        /// источника.</para>
        /// </summary>
        public string CurrentLocale
        {
            get
            {
                // ДЕЙСТВУЮЩИЙ язык считает дом: «авто» он разрешает в код
                // системы сам, и хосту не нужно ни смотреть на телефон, ни
                // записывать ответ в выбор игрока.
                var live = Lvn.UI.LvnLocale.Effective;
                return !string.IsNullOrEmpty(live) ? live : Locale;
            }
        }

        /// <summary>Язык, на котором СЕЙЧАС собран каталог главы. Не «что
        /// выбрано», а «что применено» — только для того, чтобы заметить смену.</summary>
        private string _localeApplied;

        [Tooltip("Runtime ThemeStyleSheet so the shell's text has a font.")]
        public ThemeStyleSheet ShellTheme;

        [Tooltip("Optional: Resources path to a ThemeStyleSheet, loaded when ShellTheme is unset. " +
                 "Lets you wire the theme by string (e.g. \"UI/AppLoading/UnityDefaultRuntimeTheme\").")]
        public string ThemeResourcePath = "";

        public bool AskName = true;

        [Tooltip("Player/account id for server-synced saves (/v1/state?user=…). Leave " +
                 "empty to use a per-device id generated once and kept in PlayerPrefs. " +
                 "Stats always work offline; the server is a durable cross-device backup.")]
        public string UserId = "";

        [Tooltip("Shared secret gating this user's server saves (X-State-Key). MUST be the same on every device when UserId is a cross-device account; leave empty for a per-device secret.")]
        public string StateKey = "";

        [Tooltip("Live content sync: poll the server's version endpoint this often (seconds). " +
                 "Edit a .lvn or the manifest on the server and the app reloads within one interval. " +
                 "0 disables polling.")]
        public float SyncInterval = 2f;

        private CachingAssets _assets;
        private NovelShell _shell;
        private DownloadManager _downloads;
        private ContentSync _sync;
        private ILvnStateStore _state;   // stat/var persistence (local-first, optional server sync)
        private LvnChapter _currentChapter;
        private LvnTitle _currentTitle; // the playing title — for live per-title re-theming
        private string _currentScriptJson;
        /// <summary>
        /// Имя игрока — ОКНО в дом (<see cref="Lvn.UI.LvnPlayerName"/>), а не
        /// своя копия.
        ///
        /// <para>Копий было три: настройка игрока, поле оболочки и поле хоста.
        /// Синхронизировали их руками — всюду рядом стояли две строки
        /// (<c>_playerName = nick; LvnPrefs.PlayerName = nick;</c>), и это ровно
        /// признак канона. Держалось до пути, где вспомнили не про все: имя,
        /// введённое из квик-меню, не доходило до оболочки, и следующая глава
        /// звала игрока по-старому.</para>
        /// </summary>
        private string _playerName => Lvn.UI.LvnPlayerName.Current;
        private LvnUiConfig _globalUi; // manifest.ui — the base for per-title theming
        private LvnManifest _manifest; // the live manifest (cross-chapter save routing)

        // Title-level variable declarations (title.vars_url), cached per title:
        // "game" keys persist across chapters, "chapter" keys reset on every
        // fresh chapter entry. Replaces the per-chapter default-set boilerplate.
        private sealed class TitleVars
        {
            public Newtonsoft.Json.Linq.JObject game;
            public Newtonsoft.Json.Linq.JObject chapter;
        }


        /// <summary>
        /// МЫ ТЕПЕРЬ В ЭТОЙ ГЛАВЕ — одно действие вместо трёх присваиваний.
        ///
        /// <para>Поля хоста нужны ему самому (объекты новеллы и главы), а
        /// журналам нужен тот же факт идентификаторами. Ставились они порознь
        /// и в разных файлах, и одна дорога это уже теряла: загрузка сохранения
        /// ИЗ ДРУГОЙ ГЛАВЫ обновляла поля и прогресс, но не контекст журналов —
        /// после такого прыжка аналитика и жалоба игрока указывали на
        /// предыдущую главу.</para>
        /// </summary>
        private void EnterChapterContext(LvnTitle title, LvnChapter chapter)
        {
            _currentTitle = title;
            _currentChapter = chapter;
            Lvn.Services.LvnWhereabouts.Enter(title?.id, chapter?.id);
        }

        /// <summary>Глава кончилась: и поля, и контекст журналов гаснут вместе.</summary>
        private void LeaveChapterContext()
        {
            _currentTitle = null;
            _currentChapter = null;
            Lvn.Services.LvnWhereabouts.Leave();
        }

        public CachingAssets Assets => _assets;
        public NovelShell Shell => _shell;

        /// <summary>
        /// Откуда берётся контент и где живут статы игрока.
        ///
        /// <para>Две ветки, и разница между ними принципиальная: встроенный
        /// набор играется целиком с диска (опрашивать нечего, поэтому и
        /// интервал синхронизации обнуляется), а серверная сборка синхронизирует
        /// статы через /v1/state — но местно-первым способом, чтобы игра
        /// оставалась играбельной и сохраняла прогресс, когда сервер лежит.</para>
        /// </summary>
        private string OpenContentAndState()
        {
            var contentBase = ServerUrl;
            if (OfflineBundled)
            {
                contentBase = LocalContentBase(BundleSubdir);
                SyncInterval = 0f; // nothing to poll — content is baked into the build
                Debug.Log($"[novelapp] offline bundle → {contentBase}");
            }

            _assets = new CachingAssets(contentBase);
            // Сид первого входа: онлайн-сборка везёт критичные файлы вводной в
            // APK (StreamingAssets/lvn-seed) — первая сцена одевается без сети.
            // Без index.json в билде сид просто молчит.
            if (!OfflineBundled)
                _assets.Loader.EnableSeed(LocalContentBase("lvn-seed"));

            // Stat/var persistence: a bundled offline build keeps stats locally; a
            // server build syncs through /v1/state (local-first, so it still plays and
            // keeps stats when the server is down).
            // Свёрток прогресса сливается СВОИМ правилом: пополевое слияние
            // увидело бы в нём пять ключей и отдало бы «titles» целиком одной
            // стороне, а внутри — потолок глав, галерея и закладка, у каждого
            // своя цена ошибки.
            HttpStateStore.RuleFor(ProgressVault.Scope, ProgressVault.Merge);

            _state = OfflineBundled
                ? (ILvnStateStore)new LocalStateStore()
                : new HttpStateStore(contentBase, ResolveUserId(), StateKey);
            return contentBase;
        }

        /// <summary>
        /// Продуктовый слой поверх движка: вход, реклама, кошелёк, аналитика,
        /// эксперименты и функции выражений. Возвращает адрес, откуда берётся
        /// контент, — он же решает, играем мы с сервера или из встроенного
        /// набора.
        ///
        /// <para>Порядок здесь не косметический: регистрация идёт ПЕРЕД
        /// отправкой источника перехода (без сессии сервер не знает, чей это
        /// канал), а функции выражений ставятся до первой главы, потому что
        /// читают живое состояние кошелька и гардероба.</para>
        /// </summary>
        private string InstallProductServices()
        {
            // Product services ride the same host (BaseUrl set above, before the
            // log shipper); registration is idempotent and a no-op offline — a
            // pure-offline game just never signs in.
            var contentBase = ServerUrl;
#if UNITY_EDITOR
            // Editor test doubles: the 'dev' auth provider (server -auth-dev)
            // and an instantly-"watched" rewarded ad — the full sign-in and
            // ad-reward flows run end-to-end without any store SDKs. Real
            // builds: the host plugs LvnPlatformAuth.Google/Apple and
            // LvnAds.ShowRewarded (CAS.AI etc.) instead.
            Lvn.Services.LvnPlatformAuth.Dev ??=
                () => Task.FromResult("editor-dev-" + Lvn.LvnDeviceProfile.DeviceId);
            Lvn.Services.LvnAds.ShowRewarded ??= _ => Task.FromResult(true);
#endif
            // Откуда пришёл игрок. Init ловит и холодный запуск по ссылке, и
            // переход по ссылке в уже запущенном приложении; отправка идёт
            // ПОСЛЕ регистрации — без сессии сервер не знает, чей это канал.
            Lvn.Services.LvnAttribution.Init();
            LvnAsync.Fire(RegisterThenAttributeAsync(), "RegisterThenAttribute");
            Lvn.Services.LvnServiceOps.RegisterAll(); // ext wallet_earn / leaderboard_submit / … from .lvns
            // has_item / balance / worn в выражениях: ветка за покупку.
            // Читают живое состояние, поэтому ставить их надо до первой главы,
            // а не при входе в неё.
            Lvn.Services.LvnStoryFunctions.Install();
            Lvn.Services.LvnExperiments.Install();  // abtest("имя") в выражениях
            // Хвост лога к отзыву берём из того же кольцевого буфера, что уже
            // отправляет диагностику: второй буфер — это вторая правда.
            Lvn.Services.LvnFeedback.TailLog = () => Lvn.Services.LvnLogShip.Tail();
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.Boot);
            return OpenContentAndState();
        }



        private void PrepareStage(LvnManifest manifest)
        {
            if (Stage == null)
            {
                Stage = CreateStage();
                // Шелл на этой стадии может ещё не существовать (PrepareStage
                // зовётся из Start до Build) — подписка лениво, при первом
                // живом шелле.
                if (_shell != null)
                {
                    _shell.OnMenuVisible -= ShowMenuScene;
                    _shell.OnMenuVisible += ShowMenuScene;
                }
            }
            _assets.Set3DSetCatalog(manifest.sets3d);
            Stage.Assets = _assets;
            Stage.Catalog = new SpriteCatalog(manifest.sprites);
            Stage.NameInput = manifest.ui?.name_input;   // оформление формы ввода — авторское
            // Theme the in-game dialogue/choices from the manifest, the same way
            // the shell screens read manifest.ui — so the whole game is themeable.
            // (A title can override this per-game; applied in PlayChapterAsync.)
            _globalUi = manifest.ui;
            TeachHousesFrom(manifest);
            _manifest = manifest;
            ApplyMenuStaging(manifest);
            WarmMenuCanvas();     // полотно витрины — к первому же показу меню
            WatchMenuBackdrop();  // и под наблюдение Лекаря: витрина без фона — недуг
            Stage.ApplyTheme(VnThemeBuilder.From(manifest.ui, Stage.Theme));
            Stage.CrossChapterLoader = CrossChapterLoadAsync;

            // Language: the manifest declares which catalogs exist (Settings shows
            // a picker when any); the reader's persisted choice wins over the
            // inspector default, and changing it mid-story reloads the catalog.
            // Язык устройства подставлял ЗДЕСЬ сам хост — и записывал его в
            // выбор игрока. После этого «не выбирал» было не отличить от
            // «выбрал», вернуться к языку системы нечем, а смена языка телефона
            // ничего не меняла: подстановка случалась однажды в жизни установки.
            // Теперь решает дом (LvnLocale.Effective), и «авто» — обычный
            // вариант в ряду настроек.
            // Языки объявляет дом обучения (TeachHousesFrom): их список тоже
            // меняется вместе с контентом — автор доложил перевод, и он обязан
            // появиться в настройках без перезапуска.
            // ЯЗЫК ПЕРЕЖИВАЕТ ПЕРЕЗАПУСК. Перевод накладывался только по СМЕНЕ
            // языка, а выбор хранится на устройстве: после перезапуска игрок
            // видел выбранный английский в списке — и русское меню вокруг
            // («настройки сами язык потеряли», Илья 28.08). Прогрев остальных
            // языков идёт следом, чтобы переключение было мгновенным.
            LvnAsync.Fire(ApplyLocaleAtBootAsync(), "ApplyLocale");
            _localeApplied = CurrentLocale;   // с чем стартовали — от этого и считаем смену
            LvnPrefs.Changed -= OnPrefsMaybeLocale;
            _leash.Hold(() => LvnPrefs.Changed += OnPrefsMaybeLocale,
                        () => LvnPrefs.Changed -= OnPrefsMaybeLocale);

            // ХОД ПРОГРЕССА СВОДИТ ХОСТ, а не тот, кто его сделал. Раньше
            // сведение звали руками после каждого хода — и звали не все:
            // экраны (выбор главы в списке, «переиграть», сброс новеллы)
            // двигали точку молча, и облачная копия оставалась со старой.
            LvnProgress.Moved -= SyncProgressVault;
            _leash.Hold(() => LvnProgress.Moved += SyncProgressVault,
                        () => LvnProgress.Moved -= SyncProgressVault);
        }

        /// <summary>
        /// Вход в главу: дождаться первого фона за непрозрачной загрузкой,
        /// раскрыть живую сцену, показать титул главы и — на первой главе —
        /// спросить имя.
        ///
        /// <para>Ожидание фона имеет СРОК: текстовая сцена может не иметь фона
        /// вовсе, и без срока вход завис бы навсегда. Возобновление titles не
        /// показывает: игрок посреди сцены, а не в начале.</para>
        ///
        /// <para>Обещание <c>entryDone</c> выполняется в finally ЧТО БЫ НИ
        /// СЛУЧИЛОСЬ: на нём держится первая реплика, и незакрытое обещание —
        /// это игра, замершая на пустом экране.</para>
        /// </summary>
        private async Task RevealChapterEntryAsync(LvnTitle title, LvnChapter chapter,
            bool resuming, bool restart, bool novelFreshStart,
            TaskCompletionSource<bool> entryDone)
        {
            // Liminal-style entry: the chapter has been booting BEHIND the opaque
            // loader; once the first background lands (or a short grace passes —
            // some scenes are text-only), fade the loader into the LIVE scene and
            // float the chapter title over it. A resume skips the title card (the
            // player is mid-scene, not at the opening). Chapter 2+ in a seamless
            // chain: the loader is already hidden (no-op), the title still shows.
            // ЗАНАВЕС ПЕРЕХОДА СНИМАЕТСЯ ЗДЕСЬ, А НЕ В КАТСЦЕНЕ ПРИБЫТИЯ.
            // Реплей автосейва честно восстанавливает кадр целиком — вместе с
            // затемнением, которое стояло в точке сохранения («Replay fade veil»
            // в потоке команд). Катсцена его снимала, но лишь через сотни
            // миллисекунд — ровно столько игрок и видел чёрный экран посреди
            // перехода (живой репорт 28.08). Сцену он ещё увидит; чернота между
            // мирами — нет.
            if (Portal != null)
            {
                Stage?.ApplyStage(new JObject
                {
                    ["op"] = "fade", ["to"] = "clear", ["duration"] = 0f,
                }, LvnSender.Cutscene);
                Stage?.ApplyStage(new JObject { ["op"] = "fx", ["off"] = 1 }, LvnSender.Cutscene);
            }

            float revealStart = Lvn.LvnClock.Wall();
            float revealDeadline = revealStart + (_shell?.Transitions?.backdrop_grace ?? 2f);
            while (Stage != null && !Stage.HasBackdrop && Lvn.LvnClock.Wall() < revealDeadline)
                await Task.Yield();
            Debug.Log($"[novelapp] entry reveal: backdrop={Stage?.HasBackdrop} " +
                      $"waited={(Lvn.LvnClock.Wall() - revealStart) * 1000f:F0}ms resuming={resuming}");
            // СТВОР ЗАКРЫВАЕТСЯ ИМЕННО ЗДЕСЬ, а не сразу после Play. Между Play
            // и этим местом сцена убирается ЕЩЁ РАЗ — восстановление автосейва
            // делает свой сброс, — и створ, поставленный раньше, не доживает.
            // Здесь фон главы уже на экране и уборок больше не будет: героиня
            // выходит из портала в готовый кадр.
            try
            {
                // Внутри try вместе с остальным входом: сорвись катсцена
                // прибытия — и первая реплика оставалась бы ждать вечно, потому
                // что отпускает её finally ниже.
                await ArriveInChapterAsync();
                if (_shell != null)
                {
                    await _shell.RevealFromLoadingAsync();
                    if (!resuming) await _shell.ShowChapterTitleAsync(chapter, title);
                    // ИМЯ СПРАШИВАЕТ ИСТОРИЯ, А НЕ ОБОЛОЧКА. Отдельный экран
                    // ввода снят: он вклинивался между титром главы и первой
                    // репликой формой-анкетой, и ни одна новелла им больше не
                    // пользуется. Автору доступна команда `input var=…` — тот же
                    // вопрос, но ГОЛОСОМ ПЕРСОНАЖА и в нужном месте сцены.
                }
            }
            finally { entryDone.TrySetResult(true); } // release the first line NO MATTER WHAT
        }

        /// <summary>
        /// Ровная подача кадров. Без явной цели Android отдаёт «сколько
        /// получится», и картинка идёт рывками даже там, где кадров хватает: на
        /// телефоне vSync игнорируется, а плавность считывается по РАВНОМЕРНОСТИ
        /// интервалов, а не по их числу. Шестьдесят там, где экран умеет, иначе
        /// родная частота панели.
        /// </summary>
        private static void ConfigureFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            // Настройка игрока (30 — экономия батареи), но не выше возможностей
            // экрана: на 30-герцовой панели просить 60 бессмысленно.
            Application.targetFrameRate =
                Mathf.Min(Lvn.UI.LvnPrefs.TargetFps, Lvn.LvnDeviceProfile.FpsCap());
        }

        private float ChapterLoadProgress()
        {
            var s = _chapterSched;
            if (s == null || s.RequiredReady) return 1f;
            var p = s.Progress;
            if (p > 0f) return p;
            return s.RequiredTotal > 0 ? (float)s.RequiredDone / s.RequiredTotal : 0f;
        }

        // Populate the CG gallery from every title's `gallery` (unlock state from
        // LvnGalleryStore), then open it. No items anywhere → the screen keeps its
        // built-in demo fallback.






        // Builds a VnStage on a child GameObject with its own UIDocument + panel
        // (sortingOrder below the shell's 30) so dropping a single NovelApp on an
        // empty object is enough to run the whole flow.

        private VnStage CreateStage()
        {
            var go = new GameObject("VnStage");
            go.transform.SetParent(transform, false);
            // Configure while inactive so OnEnable/Build runs only after every
            // field is set — иначе Build() прочитал бы значения по умолчанию.
            go.SetActive(false);
            var doc = go.AddComponent<UIDocument>();
            // Shared panel (see NovelShell.InitDocument) — the stage document
            // layers below the shell (10 < 30) inside the same panel.
            LvnPanel.SetTheme(ShellTheme);
            doc.panelSettings = LvnPanel.Shared;
            doc.sortingOrder = Lvn.UI.LvnFloor.Stage;
            var stage = go.AddComponent<VnStage>();
            go.SetActive(true);
            return stage;
        }

        // Build the platform-correct content base for a StreamingAssets bundle.
        // Android already yields a jar:file:// url that UnityWebRequest reads
        // straight from the APK; desktop/iOS need an explicit file:// scheme.
        private static string LocalContentBase(string sub)
        {
            var p = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(sub)) p += "/" + sub.Trim('/');
            return p.Contains("://") ? p : "file://" + p;
        }

        // Load a chapter's localization catalog (text_id → string) for the active
        // Locale from <script>.<locale>.json. Best-effort: missing catalog → null,
        // so the chapter falls back to its inline text.
        private async Task<System.Collections.Generic.IReadOnlyDictionary<string, string>> LoadCatalogAsync(string scriptUrl)
        {
            var locale = CurrentLocale;
            if (string.IsNullOrEmpty(locale) || string.IsNullOrEmpty(scriptUrl)) return null;
            var url = Lvn.Content.LvnUrl.Sibling(scriptUrl, "." + locale + ".json");
            if (_stringsCache.TryGetValue(url, out var cached)) return cached;
            try
            {
                var json = await _assets.Loader.DownloadScriptText(url, default, singleAttempt: true);
                var cat = string.IsNullOrEmpty(json) ? null : Newtonsoft.Json.JsonConvert
                    .DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(json);
                _stringsCache[url] = cat;   // второе переключение туда-обратно уже мгновенное
                return cat;
            }
            catch { _stringsCache[url] = null; return null; }
        }


        // ── Offline manifest cache ───────────────────────────────────────────────
        // The manifest is cached locally on every successful online fetch and read
        // back when the server is unreachable, so a previously-online install boots
        // straight into the menu offline (chapters then play from the disk cache).
        private const string ManifestCacheKey = "lvn_manifest_cache";







        // Stream one chapter's script and run it through the VnStage, driving the
        // HUD until it ends. Returns the chapter that actually FINISHED (it can
        // differ from the requested one — a cross-chapter save load switches the
        // stage mid-play), or null when the player left mid-chapter.
        // Адреса, о промахе которых уже отчитались: одна пропавшая картинка в
        // цикле показа даёт сотни попыток, а событие нужно одно.
        private static readonly HashSet<string> _reportedAssetFails = new HashSet<string>();

        /// <summary>
        /// Открывает новеллу, названную в ссылке запуска.
        ///
        /// <para>Разбор здесь, на клиенте, а не на сервере — в отличие от
        /// меток кампании: это маршрутизация, она нужна немедленно и никуда
        /// не записывается, поэтому ошибка разбора не становится вечной.</para>
        /// </summary>
        private void ApplyDeepLink(string url)
        {
            if (string.IsNullOrEmpty(url) || _shell == null) return;
            // Где кончается путь и начинается запрос — знает дом адресов: он же
            // отбрасывает якорь «#top», о котором здесь легко забыть.
            var q = Lvn.Content.LvnUrl.Query(url);
            if (string.IsNullOrEmpty(q)) return;

            string titleId = null, chapterId = null;
            foreach (var pair in q.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair.Substring(0, eq);
                var val = System.Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", " "));
                if (key == "title" || key == "t") titleId = val;
                else if (key == "chapter" || key == "ch") chapterId = val;
            }
            if (string.IsNullOrEmpty(titleId)) return;
            if (!string.IsNullOrEmpty(chapterId))
            {
                // Молча проглотить параметр — худший вид отказа: ссылка
                // «работает», но открывает не то, и об этом никто не узнает.
                Debug.LogWarning($"[novelapp] диплинк на главу ещё не поддержан " +
                                 $"(chapter={chapterId}) — открываю новеллу {titleId} с начала");
            }
            if (!_shell.RequestPlay(titleId))
                Debug.LogWarning($"[novelapp] диплинк: новеллы {titleId} нет в манифесте");
        }

        /// <summary>
        /// Регистрация, затем отправка канала привлечения. Порядок обязателен:
        /// без сессии сервер не знает, ЧЕЙ это канал, и запрос вернул бы 401.
        /// Не вышло — метка осталась лежать и уедет на следующем запуске.
        /// </summary>
        private static async Task RegisterThenAttributeAsync()
        {
            if (!await Lvn.Services.LvnBackend.EnsureRegisteredAsync()) return;
            await Lvn.Services.LvnAttribution.FlushAsync();
            // Группы забираем ПОСЛЕ отправки канала: таргет эксперимента может
            // быть завязан на кампанию, и спросив раньше, мы получили бы ответ
            // «этот игрок ниоткуда».
            await Lvn.Services.LvnExperiments.RefreshAsync();
        }

        /// <summary>Всё, на что подписан хост. Отписка была списком того, что
        /// надо не забыть: из пяти подписок в нём числились две, а одна была
        /// лямбдой, которую отписать нечем. Событие статическое, обработчик —
        /// метод экземпляра: пересозданный NovelApp оставлял позади себя живой
        /// объект, ссылающийся на уничтоженную оболочку.</summary>
        private readonly LvnLeash _leash = new LvnLeash();

        private void OnDestroy()
        {
            _sync?.Stop();
            _leash.Release();
            _shell?.ReleaseSubscriptions();
            // The veil is a root GameObject (it outlives this component by
            // design during boot) — a host tearing NovelApp down mid-boot must
            // not be left with an opaque, input-eating veil over its own UI.
            BootVeil.Hide();
        }



        /// <summary>
        /// Перевод слов ОБОЛОЧКИ для языка: <c>ui/words.&lt;locale&gt;.json</c> в
        /// контенте, рядом с остальными данными игры.
        ///
        /// <para>Файлом, а не полем манифеста: манифест грузится на каждом
        /// подъёме и до первого экрана, а переводы нужны только тому, кто
        /// переключил язык. Ключи — те же, что у <c>ui.words</c>: чего в
        /// переводе нет, остаётся авторским словом.</para>
        /// </summary>
        // ПЕРЕКЛЮЧЕНИЕ ЯЗЫКА ОБЯЗАНО БЫТЬ МГНОВЕННЫМ. Словарь и каталог главы
        // едут по сети, и на первом переключении игрок видел паузу в пару
        // секунд — а он переключает язык, чтобы ПРОЧИТАТЬ эту реплику, и пауза
        // читается как «не сработало» (Илья, 28.08). Поэтому оба кэшируются в
        // памяти, а объявленные языки прогреваются заранее, пока игрок ещё
        // ходит по меню.
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
            _uiWordsCache = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>();
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyDictionary<string, string>>
            _stringsCache = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyDictionary<string, string>>();



    }
}
