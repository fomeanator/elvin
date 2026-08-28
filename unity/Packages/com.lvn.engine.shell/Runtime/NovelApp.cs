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
        private string _playerName;
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
                () => Task.FromResult("editor-dev-" + SystemInfo.deviceUniqueIdentifier);
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

        /// <summary>
        /// Достать манифест — свежий с сервера, иначе последний сохранённый,
        /// иначе дождаться сети.
        ///
        /// <para>Три исхода, и каждый существует не зря: сеть есть — берём и
        /// кладём в кэш; сети нет, но кэш есть — играем офлайн; нет ни того, ни
        /// другого — держим вуаль и ждём, потому что свежая установка без сети
        /// это НЕ тупик: появится сеть — приложение стартует само.</para>
        ///
        /// <para>Средний случай тонкий: проба связи могла соврать (её трёхсекундный
        /// срок проиграл медленному первому запуску), пока сам запрос манифеста
        /// уже почти успел. Поэтому перед медленными повторами мы даём шанс
        /// запросу, который всё ещё в полёте.</para>
        /// </summary>
        private async Task<(LvnManifest manifest, bool online)> ResolveManifestAsync(
            Task<LvnManifest> manifestTask, bool online, Action<string> mark)
        {
            // Manifest: fresh from the server when online (cached for next time), else
            // the last cached copy — so a previously-online install still plays offline.
            LvnManifest manifest = null;
            if (online)
            {
                try { manifest = await manifestTask; CacheManifest(manifest); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch failed: {ex.Message} — falling back to cache");
                    online = false;
                    LvnNetworkStatus.MarkOffline("manifest fetch failed");
                }
            }
            else
                // The in-flight fetch will fail on its own timeline; observe the
                // fault so it can't surface as an unobserved-exception warning.
                _ = manifestTask.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            if (manifest == null) manifest = LoadCachedManifest();
            mark("manifest");
            BootVeil.Progress(60);
            if (manifest == null)
            {
                // The probe may have lied (its 3s deadline lost to a slow first
                // launch) while the manifest fetch itself was about to succeed —
                // give the in-flight task its chance before slow retries.
                try
                {
                    manifest = await manifestTask;
                    CacheManifest(manifest);
                    online = true;
                    LvnNetworkStatus.MarkOnline("boot manifest arrived despite failed probe");
                }
                catch { /* genuinely unreachable — recovery loop below */ }
            }
            if (manifest == null)
            {
                // A fresh install that can't reach the server is NOT a dead end:
                // hold on the veil and keep retrying — the moment the network
                // appears the app boots itself, no restart needed.
                Debug.LogWarning("[novelapp] no manifest and no cache — holding boot for connectivity");
                for (int attempt = 1; manifest == null; attempt++)
                {
                    BootVeil.Status($"нет соединения с сервером — переподключение… ({attempt})");
                    // Компонент умер (смена сцены, снос встраивателем) — уходим
                    // без манифеста: вызывающий это увидит и прекратит загрузку.
                    try { await Task.Delay(5000, destroyCancellationToken); }
                    catch (OperationCanceledException) { return (null, online); }
                    try
                    {
                        manifest = await FetchManifestAsync();
                        CacheManifest(manifest);
                        online = true;
                        LvnNetworkStatus.MarkOnline("boot manifest retry succeeded");
                    }
                    catch (Exception ex)
                    {
                        Debug.Log($"[novelapp] manifest retry {attempt}: {ex.Message}");
                    }
                }
                mark("manifest (recovered)");
                BootVeil.Progress(60, "");
            }
            return (manifest, online);
        }

        /// <summary>
        /// Сцена под манифест: ассеты, каталог спрайтов, тема диалога и язык.
        ///
        /// <para>Тема берётся из того же manifest.ui, что и экраны оболочки, —
        /// иначе игра выглядела бы двумя разными продуктами: оболочка одной
        /// темы, диалог другой.</para>
        /// </summary>
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
            // Theme the in-game dialogue/choices from the manifest, the same way
            // the shell screens read manifest.ui — so the whole game is themeable.
            // (A title can override this per-game; applied in PlayChapterAsync.)
            _globalUi = manifest.ui;
            // ЦЕННИК узнаёт, как называются деньги ЭТОЙ игры: слова
            // принадлежат автору, движок знает только форму показа.
            Lvn.UI.LvnPriceTag.Learn(manifest.ui?.currency_look);
            // И как игра зовёт безымянного игрока — тоже слово автора.
            if (!string.IsNullOrEmpty(manifest.ui?.guest_name))
                Lvn.UI.LvnPlayerName.GuestLabel = manifest.ui.guest_name;
            // …и как она зовёт главу: «Глава», «Эпизод», «Дело».
            if (!string.IsNullOrEmpty(manifest.ui?.chapter_word))
                Lvn.Content.LvnCaptions.ChapterWord = manifest.ui.chapter_word;
            // Словарь оболочки: всё, что движок пишет на экране сам.
            Lvn.Content.LvnWords.Learn(manifest.ui?.words);
            _manifest = manifest;
            ApplyMenuStaging(manifest);
            WarmMenuCanvas();     // полотно витрины — к первому же показу меню
            WatchMenuBackdrop();  // и под наблюдение Лекаря: витрина без фона — недуг
            Stage.ApplyTheme(VnThemeBuilder.From(manifest.ui, Stage.Theme));
            Stage.CrossChapterLoader = CrossChapterLoadAsync;

            // Language: the manifest declares which catalogs exist (Settings shows
            // a picker when any); the reader's persisted choice wins over the
            // inspector default, and changing it mid-story reloads the catalog.
            // Язык — тоже по устройству (пока игрок не выбрал сам): системный
            // язык с каталогом в манифесте включается на первом запуске.
            if (!LvnPrefs.LocaleChosen && manifest.languages != null)
            {
                var sys = Lvn.UI.LvnDeviceProfile.SystemLocale;
                if (!string.IsNullOrEmpty(sys) && sys != (manifest.language ?? "ru")
                    && manifest.languages.Contains(sys))
                    LvnPrefs.Locale = sys;
            }
            LvnPrefs.OriginalLocale = manifest.language ?? "ru";
            LvnPrefs.AvailableLocales = manifest.languages != null && manifest.languages.Count > 0
                ? manifest.languages : System.Array.Empty<string>();
            if (!string.IsNullOrEmpty(LvnPrefs.Locale)) Locale = LvnPrefs.Locale;
            LvnPrefs.Changed -= OnPrefsMaybeLocale;
            LvnPrefs.Changed += OnPrefsMaybeLocale;
        }

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

            float revealStart = Time.realtimeSinceStartup;
            float revealDeadline = revealStart + (_shell?.Transitions?.backdrop_grace ?? 2f);
            while (Stage != null && !Stage.HasBackdrop && Time.realtimeSinceStartup < revealDeadline)
                await Task.Yield();
            Debug.Log($"[novelapp] entry reveal: backdrop={Stage?.HasBackdrop} " +
                      $"waited={(Time.realtimeSinceStartup - revealStart) * 1000f:F0}ms resuming={resuming}");
            // СТВОР ЗАКРЫВАЕТСЯ ИМЕННО ЗДЕСЬ, а не сразу после Play. Между Play
            // и этим местом сцена убирается ЕЩЁ РАЗ — восстановление автосейва
            // делает свой сброс, — и створ, поставленный раньше, не доживает.
            // Здесь фон главы уже на экране и уборок больше не будет: героиня
            // выходит из портала в готовый кадр.
            await ArriveInChapterAsync();
            try
            {
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
                Mathf.Min(Lvn.UI.LvnPrefs.TargetFps, Lvn.UI.LvnDeviceProfile.FpsCap());
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
            doc.sortingOrder = 10;
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
            if (string.IsNullOrEmpty(Locale) || string.IsNullOrEmpty(scriptUrl)) return null;
            var baseUrl = scriptUrl.EndsWith(".lvn") ? scriptUrl.Substring(0, scriptUrl.Length - 4) : scriptUrl;
            var url = baseUrl + "." + Locale + ".json";
            try
            {
                var json = await _assets.Loader.DownloadScriptText(url, default, singleAttempt: true);
                if (string.IsNullOrEmpty(json)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(json);
            }
            catch { return null; }
        }

        private async Task<LvnManifest> FetchManifestAsync()
        {
            // The manifest is the boot's single point of truth — a fresh install
            // has nothing without it. One transient failure (flaky emulator NAT,
            // a mid-handshake reset) must not fall through to "no manifest":
            // three quick attempts before the caller's slower recovery paths.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var json = await _assets.Loader.DownloadScriptText("/v1/content/manifest", default, singleAttempt: true);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json) ?? new LvnManifest();
                }
                catch (Exception ex) when (attempt < 3)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch attempt {attempt} failed: {ex.Message} — retrying");
                    await Task.Delay(700 * attempt);
                }
            }
        }

        // ── Offline manifest cache ───────────────────────────────────────────────
        // The manifest is cached locally on every successful online fetch and read
        // back when the server is unreachable, so a previously-online install boots
        // straight into the menu offline (chapters then play from the disk cache).
        private const string ManifestCacheKey = "lvn_manifest_cache";

        private static void CacheManifest(LvnManifest m)
        {
            if (m == null) return;
            try
            {
                LvnKeep.Put(ManifestCacheKey, Newtonsoft.Json.JsonConvert.SerializeObject(m));
            }
            catch { /* cache write best-effort */ }
        }

        private static LvnManifest LoadCachedManifest()
        {
            try
            {
                var json = LvnKeep.Get(ManifestCacheKey, null);
                return string.IsNullOrEmpty(json)
                    ? null
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            }
            catch { return null; }
        }





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
            var q = url;
            int qm = q.IndexOf('?');
            if (qm < 0) return;
            q = q.Substring(qm + 1);
            int hash = q.IndexOf('#');
            if (hash >= 0) q = q.Substring(0, hash);

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

        private void OnDestroy()
        {
            _sync?.Stop();
            LvnPrefs.Changed -= OnPrefsMaybeLocale;
            // Событие статическое, а обработчик — метод экземпляра: без этой
            // отписки пересозданный NovelApp оставил бы позади себя живой
            // объект, ссылающийся на уничтоженную оболочку.
            Lvn.Services.LvnAttribution.LinkOpened -= ApplyDeepLink;
            // The veil is a root GameObject (it outlives this component by
            // design during boot) — a host tearing NovelApp down mid-boot must
            // not be left with an opaque, input-eating veil over its own UI.
            BootVeil.Hide();
        }

        // The Settings language row writes LvnPrefs.Locale; pick the change up
        // and swap the running chapter's string catalog — new lines render in
        // the new language immediately (the visible line updates on advance).
        private async void OnPrefsMaybeLocale()
        {
            var want = LvnPrefs.Locale;
            if (want == Locale) return;
            Locale = want;
            if (_currentChapter != null && Stage != null)
            {
                try { Stage.Strings = await LoadCatalogAsync(_currentChapter.script_url); }
                catch { Stage.Strings = null; } // no catalog → the inline original
                // РЕАЛТАЙМ: реплика, уже стоящая на экране, перерисовывается
                // новым языком сразу (штатный RerenderCurrent — тот же вариант
                // текста, без сдвига {a|b|c}), а не со следующей строки.
                Stage.Player?.RerenderCurrent();
            }
        }
    }
}
