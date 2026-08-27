using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Content;
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


        public CachingAssets Assets => _assets;
        public NovelShell Shell => _shell;

        /// <summary>
        /// Ровная подача кадров. Без явной цели Android отдаёт «сколько
        /// получится», и картинка идёт рывками даже там, где кадров хватает: на
        /// телефоне vSync игнорируется, а плавность считывается по РАВНОМЕРНОСТИ
        /// интервалов, а не по их числу. Шестьдесят там, где экран умеет, иначе
        /// родная частота панели.
        /// </summary>
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
            Lvn.Services.LvnAnalytics.Track("boot");
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
            _manifest = manifest;
            ApplyMenuStaging(manifest);
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

        /// <summary>
        /// Пункты быстрого меню и хостовые опы, которые их зовут: магазин,
        /// гардероб, настройки, тестовый кран валюты, режим разглядывания арта.
        ///
        /// <para>Каждый пункт появляется, только если манифест его просит: игра
        /// без валюты не должна показывать магазин, а новелла без гардероба —
        /// пункт гардероба. Оболочка здесь ничего не знает про конкретный
        /// продукт, она лишь читает объявленное.</para>
        /// </summary>
        private void WireQuickMenu(LvnManifest manifest)
        {

            // The currency store: a quick-menu entry when the manifest opts in
            // (ui.store present), and the `ext store_show` op for scripts —
            // the story holds while the shop is open, then rolls on.
            var storeCfg = manifest.ui?.store;
            if (storeCfg != null && (storeCfg.show_menu_item ?? true))
                StageMenu.AddMenuItem(storeCfg.menu_label ?? "Store", stage => LvnAsync.Fire(_shell.OpenPackShopAsync(), "OpenPackShop"));
            Lvn.LvnOps.Register("store_show", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(OpenStoreFromScriptAsync(ctx), "OpenStoreFromScript");
            });

            // The wardrobe: a quick-menu entry when any character has one (or
            // ui.wardrobe opts in explicitly), and `ext wardrobe_show char=id`.
            // The menu entry opens the IN-STORY sheet over the live scene — the
            // hero dressed against the current background, the same experience
            // as a story wardrobe moment, but always reachable.
            var wardrobeCfg = manifest.ui?.wardrobe;
            if ((wardrobeCfg != null || AnyWardrobeEntity())
                && (wardrobeCfg?.show_menu_item ?? true))
                StageMenu.AddMenuItem(wardrobeCfg?.menu_label ?? "Wardrobe",
                    stage => LvnAsync.Fire(OpenWardrobeFromMenuAsync(stage), "OpenWardrobeFromMenu"));
            Lvn.LvnOps.Register("wardrobe_show", (cmd, ctx) =>
            {
                ctx.Hold();
                // Default: the in-story bottom sheet (the live actor is the
                // mirror). mode=full opens the full-screen overlay instead.
                LvnAsync.Fire(OpenWardrobeFromScriptAsync((string)cmd["char"], (string)cmd["mode"] == "full", ctx), "OpenWardrobeFromScript");
            });

            // ВХОД В АККАУНТ — ШАГОМ ИСТОРИИ. Гардероб и покупки живут на
            // сервере, поэтому пускать в них имеет смысл после входа. Раньше
            // экран входа показывался один раз на первом запуске, ДО того как
            // игрок понял, зачем это; теперь его зовёт сцена — там, где он
            // осмыслен. Уже вошедшего не переспрашиваем.
            Lvn.LvnOps.Register("auth_show", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(AuthFromScriptAsync(ctx), "AuthFromScript");
            });

            // РАЗРЕШЕНИЕ НА УВЕДОМЛЕНИЯ — ТОЖЕ ШАГОМ ИСТОРИИ. Системный диалог
            // конвертирует, когда у игрока есть мотив сказать «да»; сцена
            // подводит к нему (персонаж предлагает «оставаться на связи») и
            // зовёт `ext push_ask` ровно в этот момент. Уже выданное разрешение
            // не переспрашивается; платформа без запроса — тихий no-op, история
            // продолжается в любом случае.
            Lvn.LvnOps.Register("push_ask", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(PushAskFromScriptAsync(ctx), "PushAskFromScript");
            });

            // The app-level settings screen: `ext settings_show` for scripts, and
            // an opt-in quick-menu entry (default OFF — the quick menu already has
            // its own in-game playback settings; set ui.settings.show_menu_item to
            // surface this fuller screen there too).
            var settingsCfg = manifest.ui?.settings;
            if (settingsCfg != null && (settingsCfg.show_menu_item ?? false))
                StageMenu.AddMenuItem(settingsCfg.menu_label ?? "Settings", stage => LvnAsync.Fire(_shell.OpenSettingsAsync(), "OpenSettings"));

            // Wallet-priced choices (imported "[premium]" options carry
            // wallet_cost): route the spend through the product wallet. A failed
            // spend keeps the menu up — the stage shows a "not enough" hint.
            Stage.ChoiceSpend = (currency, amount) =>
                Lvn.Services.LvnWallet.SpendAsync(currency, amount, "choice");

            // Test-build currency faucet (economy.debug_grant): a quick-menu item
            // that credits the wallet on tap — the partner's "получить 100" button
            // for exercising paid choices and the wardrobe without a store.
            var faucet = manifest.economy?.debug_grant;
            if (faucet != null && !string.IsNullOrEmpty(faucet.currency))
            {
                int amount = faucet.amount ?? 100;
                string label = faucet.label ?? $"Получить {amount}";
                StageMenu.AddMenuItem(label, stage => LvnAsync.Fire(GrantFaucetAsync(faucet.currency, amount), "GrantFaucet"));
            }
            Lvn.LvnOps.Register("settings_show", (cmd, ctx) =>
            {
                ctx.Hold();
                LvnAsync.Fire(OpenSettingsFromScriptAsync(ctx), "OpenSettingsFromScript");
            });

            // The long-press art view hides the stage's chrome; mirror it onto the
            // shell HUD (a separate UIDocument) so the WHOLE screen is just the scene.
            Stage.ChromeHiddenChanged += hidden =>
            {
                if (_shell?.Hud != null)
                    _shell.Hud.style.visibility = hidden
                        ? UnityEngine.UIElements.Visibility.Hidden
                        : UnityEngine.UIElements.Visibility.Visible;
            };

            // ui.hud.mode == "choices": corner-minimal reading — the HUD stays off
            // the reading surface and surfaces exactly while a choice is up (the
            // one moment costs and balances matter). Chapter end hides it again
            // via the shell's normal Hide(Hud).
            if (_shell.HudChoicesOnly)
                Stage.ChoicesVisibleChanged += visible =>
                {
                    if (_shell?.Hud != null)
                        _shell.Hud.style.display = visible
                            ? UnityEngine.UIElements.DisplayStyle.Flex
                            : UnityEngine.UIElements.DisplayStyle.None;
                };

            // Live content sync — poll the version endpoint; reload on change.
            if (SyncInterval > 0f)
            {
                _sync = new ContentSync(_assets.Loader)
                {
                    IntervalSeconds = SyncInterval,
                    // Reconcile once immediately after the long boot. Without this,
                    // an edit made after the chapter fetch but before ContentSync
                    // starts becomes the first baseline and is never hot-reloaded.
                    NotifyOnFirstPoll = true,
                };
                _sync.OnChanged += OnContentChanged;
                _sync.Start();
            }

            // МУЗЫКА МЕНЮ (ui.browse.music): играет везде, кроме самой новеллы.
            // Глушится хуками сессии главы — воронка, стартующая с порога,
            // музыку меню не услышит вовсе, и это правильно.
            // Качество арта: настройка игрока ведёт бокс показа (@2k/@1k) —
            // синхронизируем до первой загрузки и на каждом изменении.
            DownloadPolicy.PreferredSuffix = "@" + EffectiveArtQuality();
            Lvn.UI.LvnPrefs.Changed += () =>
            {
                if (!_chapterPlaying) ShowMenuScene(); // смена фаворита — живьём
                var next = "@" + EffectiveArtQuality();
                if (DownloadPolicy.PreferredSuffix != next)
                {
                    DownloadPolicy.PreferredSuffix = next;
                    // Смена ступени экономит место и перекачивает скачанное
                    // (очередью центра загрузок) — оба чужих бокса вычищаются.
                    LvnAsync.Fire(PurgeOtherArtBoxAsync(next), "PurgeArtBox");
                    // ВИДИМАЯ сцена перекачивается сразу: фон и актёры заново
                    // грузят спрайты с новым суффиксом (репорт «героиню не
                    // перекачала» — раньше качество действовало лишь на
                    // будущие показы).
                    Stage?.RefreshArtQuality();
                }
                ConfigureFrameRate(); // 30/60 из настроек — применяется сразу
            };

            // Зум к зоне скина (Илья 28.08: «как к лицу фаворитов в прологе»):
            // лист гардероба сообщает активный раздел — камера наезжает на
            // голову/шею/корпус куклы; «Все», «Во весь рост» и закрытие
            // возвращают общий план.
            Lvn.UI.Screens.WardrobeSheet.SectionFocus -= OnWardrobeSection;
            Lvn.UI.Screens.WardrobeSheet.SectionFocus += OnWardrobeSection;

            var menuTrack = ResolveMenuTrackUrl(manifest);
            if (!string.IsNullOrEmpty(menuTrack))
            {
                _shell.OnMenuVisible -= ShowMenuScene;
                _shell.OnMenuVisible += ShowMenuScene; // сцена меню по факту показа хаба
                _shell.OnTabTravel = PanMenuScene;     // полотно панорамирует с вкладками
                _shell.OnTabTravelTick = k =>          // …кадр в кадр с UI
                {
                    if (_chapterPlaying || Stage == null || !_menuBgSet) return;
                    Stage.SetBackgroundPan(Mathf.Lerp(_menuPanFrom, _menuPanTo, k));
                };
                // Смена наряда в гардеробе не должна ронять фон (живой скрин:
                // Equip стирал полотно) — пере-ставим сцену меню следом.
                Lvn.UI.LvnWardrobe.Changed += _ => { if (!_chapterPlaying) ShowMenuScene(); };
                _shell.OnChapterSessionStart += () => { Lvn.UI.LvnScreenDirector.Current.EnterChapter(); _menuBgSet = false; _menuMusic?.Pause(); HideMenuSceneActor(); };
                _shell.OnChapterSessionEnd += () =>
                {
                    Lvn.UI.LvnScreenDirector.Current.LeaveChapter();
                    if (_menuMusic != null && _menuMusic.clip != null) _menuMusic.UnPause();
                };
                LvnAsync.Fire(StartMenuMusicAsync(menuTrack), "MenuMusic");
            }

            // Hub browse flow (ui.browse.layout = "hub"): unlock conditions read the
            // player's global stat flags; Play charges the title's entry cost; a
            // locked card explains itself with a popup.
            if (_shell.Hub != null)
            {
                _shell.Hub.GlobalStatsProvider = () => _state.LoadVarsAsync(GlobalScopeId, default);
                _shell.Hub.OnPlay = ChargeTitleEntryAsync;
                _shell.Hub.OnLockedHint = (name, hint) =>
                    _shell.AlertAsync(name, string.IsNullOrEmpty(hint) ? "Locked" : hint);
                _shell.Hub.OnMenu = () => _shell.OpenSettingsAsync(); // avatar → account/settings
                _shell.Hub.OnStore = () => _shell.TabGoTo(1);   // вкладка ленты
                // Гардероб → the REAL, wallet-synced wardrobe for the game's main
                // heroine (title.hero ?? manifest.hero). Ownership lives in the
                // shared LvnWallet.Inventory, so it stays in sync with the in-story
                // wardrobe. (The prettier SkinShop screen gets wired to this same
                // data next.)
                // ЛЕНТА ВКЛАДОК (Илья 26.08): Главная(0) → Магазин(1) →
                // Гардероб(2) → Профиль(3). Переход едет по ленте: хаб уезжает,
                // промежуточные вкладки ПРОЛЕТАЮТ через кадр, цель въезжает.
                // ГАРДЕРОБ ОДИН: в меню — вкладка вокруг общей героини,
                // в игре — прежний сценический шит (квик-меню/оп wardrobe_show).
                _shell.Hub.OnWardrobe = () => _shell.TabGoTo(2);
                _shell.Hub.OnGallery = OpenGalleryForRealAsync;
                _shell.Hub.OnProfile = () => OpenProfileWithRelationsAsync();
                // TR-25: партнёр прячет ежедневную награду данными; сама
                // кнопка скрывается в BrowseHub по тому же конфигу.
                if (manifest.ui?.browse?.show_daily ?? true)
                    _shell.Hub.OnDaily = () => _shell.OpenDailyAsync();
                _shell.Hub.PlayerName = _playerName;
                _shell.Hub.Currencies = HubCurrencies();
                _shell.Hub.ExternalTopBar = true; // валюты несёт единый навбар
                _shell.Hub.OnHomeNav = () => LvnAsync.Fire(_shell.TabGoTo(0), "TabHome");
                // Tapping a card opens the rich detail page seeded with this title.
                _shell.Hub.OnOpenDetail = t => OpenDetailWithStatsAsync(t);
            }

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
            float revealStart = Time.realtimeSinceStartup;
            float revealDeadline = revealStart + (_shell?.Transitions?.backdrop_grace ?? 2f);
            while (Stage != null && !Stage.HasBackdrop && Time.realtimeSinceStartup < revealDeadline)
                await Task.Yield();
            Debug.Log($"[novelapp] entry reveal: backdrop={Stage?.HasBackdrop} " +
                      $"waited={(Time.realtimeSinceStartup - revealStart) * 1000f:F0}ms resuming={resuming}");
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

        private static void ConfigureFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            // Настройка игрока (30 — экономия батареи), но не выше возможностей
            // экрана: на 30-герцовой панели просить 60 бессмысленно.
            Application.targetFrameRate =
                Mathf.Min(Lvn.UI.LvnPrefs.TargetFps, Lvn.UI.LvnDeviceProfile.FpsCap());
        }

        /// <summary>
        /// Подписки на то, что рассказывает о себе история: промахи ассетов и
        /// шаги внутри главы (метки — «слайды» автора, выборы — места, где
        /// решает игрок). Без них между входом в главу и выходом пусто, и на
        /// вопрос «где отваливаются» отвечать нечем.
        ///
        /// <para>Каждая подписка сперва снимается: Start у долгоживущего
        /// объекта может пройти дважды (пересоздание оболочки), и без снятия
        /// одно событие обрабатывалось бы дважды.</para>
        /// </summary>
        private void SubscribeStoryDiagnostics()
        {
            Lvn.Content.ContentLoader.AssetFailed -= OnAssetFailed;
            Lvn.Content.ContentLoader.AssetFailed += OnAssetFailed;

            // Шаги внутри главы: метки — это «слайды» автора, выборы — места,
            // где игрок решает. Без них между входом в главу и выходом пусто, и
            // на вопрос «где отваливаются» отвечать нечем.
            LvnPlayer.LabelReached -= OnLabelReached;
            LvnPlayer.LabelReached += OnLabelReached;
            LvnPlayer.ChoiceShown -= OnChoiceShown;
            LvnPlayer.ChoiceShown += OnChoiceShown;
            LvnPlayer.ChoicePicked -= OnChoicePicked;
            LvnPlayer.ChoicePicked += OnChoicePicked;
        }

        private async void Start()
        {
            ConfigureFrameRate();

            // Boot telemetry: one stopwatch, a mark per phase — `adb logcat -s
            // Unity | grep lvn-boot` (or the editor console) reads as a boot
            // profile. Anything that grows here is a regression to hunt.
            var bootClock = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string phase) => Debug.Log($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms {phase}");

            // Мост к хост-приложению. Поднимаем ВСЕГДА: в самостоятельной
            // сборке он молчит (отправка никуда не подключена), а когда движок
            // собран библиотекой — хост должен найти его сразу, не дожидаясь
            // первой главы. Иначе первые сообщения уходят в пустоту, и «Unity
            // не отвечает» выглядит как поломка канала, а не как гонка.
            LvnHostBridge.Ensure(this);

            // Test-lane server override (Development builds only): device
            // automation points this install at a throwaway server via
            // `am start … -e lvn_server <url>` (or LVN_SERVER for CI players)
            // instead of re-exporting. Must land before ANYTHING derives from
            // ServerUrl — the log shipper, content base and state store all do.
            var serverOverride = LvnLaunchOverrides.ServerUrl();

            // The theme must land on the shared panel BEFORE the veil: a panel
            // without a ThemeStyleSheet has no default font, so every veil
            // label renders as NOTHING — the "black screen with no text" class
            // of bug. (The shell used to set it only after the manifest.)
            if (ShellTheme == null && !string.IsNullOrEmpty(ThemeResourcePath))
                ShellTheme = Resources.Load<ThemeStyleSheet>(ThemeResourcePath);
            LvnPanel.SetTheme(ShellTheme);

            // First paint THIS frame — before any network round-trip — so the
            // device never sits on a raw black screen while boot works.
            BootVeil.Show();
            Mark("veil up (first paint)");
            // Штамп сборки: время последней компиляции каждой Lvn-сборки.
            // Отвечает на вечный вопрос «а этот прогон вообще на новом коде?»
            // без раскопок в Library/ScriptAssemblies.
            Debug.Log(Lvn.LvnBuildStamp.Line(
                typeof(Lvn.LvnPlayer), typeof(VnStage),
                typeof(Lvn.Content.ContentLoader), typeof(NovelApp)));
            // Let the veil actually REACH the screen before any heavier boot
            // work (PSO load, probes): on slow devices frame 1's render was
            // getting starved and the first visible percent was already 30.
            await Task.Yield();
            await Task.Yield();

            if (serverOverride != null)
            {
                // Test-lane override always wins — it exists so device automation
                // can point an install at a throwaway server without re-exporting.
                ServerUrl = serverOverride;
                Debug.Log($"[novelapp] server override (dev): {ServerUrl}");
            }
            else
            {
                // CS-1.6-style server pick, over the veil: unchecked (default),
                // the known servers race a /healthz ping and the first live one
                // wins — invisible unless nothing answers in time. Checked
                // (persisted), a small browser lists them plus a free-text field
                // for the player's own host, and waits for an explicit Connect.
                ServerUrl = await ServerSelectScreen.ResolveAsync(ServerUrl, KnownServers, destroyCancellationToken);
                Debug.Log($"[novelapp] server resolved: {ServerUrl}");
            }
            Mark("server resolved");

            // Field diagnostics BEFORE the first mark: errors, exceptions and
            // the [lvn-boot]/[lvn-perf] marks ship to /v1/log/client — a partner
            // device's crash is readable via /v1/admin/client-logs, no adb.
            Lvn.Services.LvnBackend.BaseUrl = ServerUrl;
            Lvn.Services.LvnLogShip.Boot();

            // Промахи ассетов — в аналитику. Движок про неё не знает и знать не
            // должен, поэтому он лишь сообщает о неудаче, а отнести её к новелле
            // и главе умеет только оболочка. Дедупликация по адресу: одна
            // пропавшая картинка в цикле показа даёт сотни попыток, и без неё
            // очередь событий забьётся одним и тем же.
            SubscribeStoryDiagnostics();

            // PSO precook: warms last session's traced pipeline states behind
            // the boot screen (first launch traces instead) — kills the
            // first-show shader-compile hitches. Fire-and-forget, self-paced.
            LvnPsoWarmup.Boot();

            var contentBase = InstallProductServices();
            // Connectivity gate (Liminal-style): probe the server with a hard 3s
            // deadline so an unreachable server falls straight through to the offline
            // path instead of hanging on a stuck socket. A local/bundled origin is
            // always reachable. The probe pins the global offline flag so every later
            // fetch fast-fails into the disk cache.
            //
            // All three boot round-trips fly TOGETHER — healthz, the version
            // index and the manifest are independent GETs, and running them
            // serially was the single biggest boot cost on device (3 × mobile
            // RTT; the old worst case even ate the probe's full 3s deadline
            // before the first byte of manifest moved).
            var probeTask = _assets.Loader.IsLocal ? Task.FromResult(true) : ProbeOnlineAsync();
            var versionsTask = _assets.WarmVersionsAsync();
            var manifestTask = FetchManifestAsync();
            BootVeil.Progress(10, "подключение…");

            bool online = await probeTask;
            if (!online) LvnNetworkStatus.MarkOffline("boot healthz: server unreachable");
            Mark($"connectivity → {(online ? "online" : "offline")}");
            BootVeil.Progress(30, "загрузка данных…");

            try { await versionsTask; } catch { /* offline: last-known index */ }
            Mark("version index");

            var boot = await ResolveManifestAsync(manifestTask, online, Mark);
            var manifest = boot.manifest;
            online = boot.online;
            // The awaits above outlive a destroyed host (scene switch, embedder
            // teardown) — never keep booting on a dead component. Пустой манифест
            // означает ровно это: ожидание сети прервали сносом компонента.
            if (destroyCancellationToken.IsCancellationRequested || manifest == null) return;
            Debug.Log($"[novelapp] manifest: {manifest.titles?.Count ?? 0} title(s) (online={online})");

            PrepareStage(manifest);
            Mark("stage + theme ready");
            _downloads = new DownloadManager(_assets.Loader);
            var prefetch = SafeBootPrefetch(manifest, online);
            _ = prefetch.ContinueWith(_ => Debug.Log(
                $"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms boot prefetch settled (background)"),
                TaskScheduler.FromCurrentSynchronizationContext());

            // Progress vault: a VIRGIN install (corrupted prefs, a reinstall
            // under the same identity) gets the player's progress re-planted —
            // file home first (instant, offline), then the server backup —
            // BEFORE the hub renders, so «Продолжить» is right from frame one.
            try
            {
                if (ProgressVault.IsVirgin(manifest))
                {
                    ProgressVault.Apply(ProgressVault.ReadLocal(), manifest);
                    if (ProgressVault.IsVirgin(manifest) && _state != null)
                        ProgressVault.Apply(
                            await _state.LoadVarsAsync(ProgressVault.Scope, destroyCancellationToken),
                            manifest);
                }
            }
            catch (Exception e) { Debug.LogWarning("[vault] restore skipped: " + e.Message); }

            _shell = NovelShell.Create(transform, 30, ShellTheme);
            _shell.Build(manifest, _assets);
            Mark("shell built");
            WireQuickMenu(manifest);

            // Чистка витрины по данным (TR-25/32).
            var browseCfg = manifest.ui?.browse;
            if (_shell.Detail != null)
                _shell.Detail.ShowSaves = browseCfg?.detail_saves ?? true;
            if (_shell.Profile != null)
                _shell.Profile.Minimal = !(browseCfg?.profile_full ?? true);

            // «Скачать всю игру» в настройках: оценка/батч/прогресс/очистка —
            // всё из лоадера, экран только рисует (ELVIN-85).
            if (_shell.Settings != null)
            {
                var loader = _assets.Loader;
                var opts = manifest.ui?.browse?.music_options;
                if (opts != null && opts.Count > 0)
                {
                    var lst = new List<(string id, string title)>();
                    foreach (var o in opts)
                        if (o != null && !string.IsNullOrEmpty(o.id))
                            lst.Add((o.id, string.IsNullOrEmpty(o.title) ? o.id : o.title));
                    _shell.Settings.MenuTracks = lst;
                    _shell.Settings.OnMenuTrack = id =>
                        LvnAsync.Fire(SwitchMenuTrackAsync(ResolveMenuTrackUrl(manifest)), "SwitchMenuTrack");
                }
                // Паспорт устройства → серверный профиль игрока (сегменты,
                // саппорт «на чём играет»), как делают все крупные аналитики.
                Lvn.Services.LvnAnalytics.Track("device", Lvn.UI.LvnDeviceProfile.Snapshot());

                _shell.Settings.StorageInfo = StorageInfoAsync;
                _shell.Settings.DownloadAll = DownloadEverythingAsync;
                _shell.Settings.ClearDownloads = async () =>
                {
                    long freed = await loader.ClearAssetCacheAsync();
                    Debug.Log($"[content] загруженное удалено: {freed >> 20} МБ");
                };
                _shell.Settings.DownloadProgress = () =>
                    (loader.BatchBytesReceived, loader.BatchBytesExpected, loader.BatchActive);

                // Единый навбар: валюты данными, бургер по контексту
                // (в сцене — квик-меню, в меню — настройки), пилюля — магазин.
                if (_shell.TopBar != null)
                {
                    _shell.TopBar.Currencies = HubCurrencies();
                    _shell.TopBar.RefreshBalances();
                    _shell.TopBar.OnCurrency = _ => LvnAsync.Fire(_shell.OpenPackShopAsync(), "TopBarStore");
                    _shell.TopBar.OnBurger = () =>
                    {
                        if (_chapterPlaying && Stage != null) Stage.OpenQuickMenu();
                        else LvnAsync.Fire(_shell.OpenSettingsAsync(), "TopBarSettings");
                    };
                    Lvn.UI.StageMenu.ExternalSettings = () =>
                        LvnAsync.Fire(_shell.OpenSettingsAsync(), "UnifiedSettings");
                    // Бургер-фаб в сцене убран (Илья 26.08): выезжающий игровой
                    // бар по тапу верхней зоны несёт 4 кнопки.
                    Lvn.UI.StageMenu.ExternalBurger = true;
                    _shell.TopBar.TapZoneAvailable = () =>
                        Stage == null || (!Stage.InputBlocked && !Stage.PanelOpen);
                    _shell.TopBar.OnGameExit = () => Stage?.RequestExit();
                    // Воронка: в интро навбар полностью нем (чистое кино).
                    _shell.OnChapterSessionStart += () => _shell.TopBar.SetSilent(
                        string.Equals(_currentTitle?.type, "intro", StringComparison.OrdinalIgnoreCase));
                    _shell.TopBar.OnGameHistory = () => Stage?.OpenQuickMenu("history");
                    _shell.TopBar.OnGameWardrobe = () =>
                    { if (Stage != null) LvnAsync.Fire(OpenWardrobeFromMenuAsync(Stage), "OpenWardrobeFromMenu"); };
                    _shell.TopBar.OnGameStore = () =>
                        LvnAsync.Fire(_shell.OpenPackShopAsync(), "GameBarStore");
                }

                // Центр загрузок: очередь по главам + данные для попапа
                // индикатора (офлайн-правила, синк, «скачать всё»).
                if (_shell.DownloadHud != null)
                {
                    _dlCenter ??= new Lvn.UI.Screens.DownloadCenter(loader);
                    var hud = _shell.DownloadHud;
                    hud.Center = _dlCenter;
                    hud.Offline = () => Lvn.Content.LvnNetworkStatus.IsOffline;
                    hud.PendingOps = () => Lvn.Services.LvnWallet.PendingCount;
                    hud.ActiveUrl = () => loader.LastStartedUrl;
                    hud.FlushPending = Lvn.Services.LvnWallet.FlushAsync;
                    hud.DownloadAll = DownloadEverythingAsync;
                    hud.ChaptersInfo = ChapterAvailability;
                    hud.CurrentChapterOffer = () =>
                    {
                        // Только во время сессии: вне игры «текущая глава» —
                        // хвост прошлого запуска («Скачать главу 0», скрин).
                        if (!_chapterPlaying) return null;
                        var t = _currentTitle; var ch = _currentChapter;
                        if (t == null || ch == null) return null;
                        long bytes = 0; int miss = 0;
                        void Probe(string url, string kind, long size)
                        {
                            if (string.IsNullOrEmpty(url)) return;
                            var eff = kind == "sprite" ? (DownloadPolicy.DownscaleVariant(url) ?? url) : url;
                            if (loader.IsAssetCached(eff)) return;
                            miss++; bytes += size > 0 ? size : 64 << 10;
                        }
                        Probe(ch.script_url, "script", 0);
                        Probe(ch.bg_url, "sprite", 0);
                        if (ch.assets != null)
                            foreach (var kv in ch.assets)
                                Probe(kv.Key, kv.Value?.kind ?? "sprite", kv.Value?.size ?? 0);
                        if (miss == 0) return null;
                        string label = $"Скачать главу {ch.number} · ≈{Mathf.Max(1, bytes >> 20)} МБ";
                        return (label, () => EnqueueChapterDownload(t, ch));
                    };
                    hud.HasSomeDownloaded = () =>
                    {
                        foreach (var (url, _, _) in CollectContentItems())
                            if (loader.IsAssetCached(url)) return true;
                        return false;
                    };
                    hud.MissingInfo = () =>
                    {
                        long bytes = 0; int files = 0;
                        var sample = new List<string>();
                        foreach (var (url, _, size) in CollectContentItems())
                            if (!loader.IsAssetCached(url))
                            {
                                bytes += size > 0 ? size : 64 << 10;
                                files++;
                                if (sample.Count < 8) sample.Add(url);
                            }
                        // Диагностика хвоста: если «скачал всё, а остаток не 0» —
                        // консоль называет виновников поимённо.
                        if (files != _lastMissingCount)
                        {
                            _lastMissingCount = files;
                            if (files > 0 && files <= 24)
                                Debug.Log($"[content] недокачано {files}: {string.Join(", ", sample)}");
                        }
                        return (bytes, files);
                    };
                }
            }

            // The FULL library warms in the background from here on: every
            // chapter of every title lands on disk while the player browses or
            // reads — the next chapter's loading screen is then near-instant,
            // and nothing EVER trickles in on camera. Yields to an active
            // chapter gate so it never steals that bandwidth.
            LvnAsync.Fire(WarmLibraryAsync(manifest, destroyCancellationToken), "WarmLibrary");
            // The veil OWNS the whole app boot — one continuous surface from
            // the first frame to the first interactive screen. The shell's own
            // boot splash is suppressed (bootSplash: false): a second loading
            // screen under the veil would flash a second bar at the hand-off.
            // The veil walks 60→100% with the real boot-prefetch progress and
            // cross-fades into the menu.
            LvnAsync.Fire(DriveBootVeilAsync(prefetch, bootClock), "DriveBootVeil");
            // Диплинк В КОНТЕНТ: ссылка вида …?title=cold открывает новеллу
            // сразу, минуя хаб. Шов RequestPlay заведён ровно для этого и до
            // сих пор пустовал. Ссылку, нажатую при уже запущенной игре, ловим
            // тем же обработчиком.
            ApplyDeepLink(Lvn.Services.LvnAttribution.LaunchUrl);
            Lvn.Services.LvnAttribution.LinkOpened -= ApplyDeepLink;
            Lvn.Services.LvnAttribution.LinkOpened += ApplyDeepLink;

            var run = _shell.RunAsync(
                bootReady: () => prefetch.IsCompleted,
                chapterReady: BeginChapterLoading,
                chapterProgress: ch => ChapterLoadProgress,
                playChapter: PlayChapterAsync,
                askName: AskName,
                ct: destroyCancellationToken,
                bootSplash: false);
            await run;
        }

        // Walks the boot veil's last stretch (60→100%) with the real boot
        // prefetch, then cross-fades the veil into the first interactive screen.
        // Catch-all by design: this is fire-and-forget, and an exception here
        // would otherwise leave an opaque veil over the app forever.
        private async Task DriveBootVeilAsync(Task prefetch, System.Diagnostics.Stopwatch bootClock)
        {
            try
            {
                var l = _assets?.Loader;
                var ct = destroyCancellationToken;
                while (!prefetch.IsCompleted && !ct.IsCancellationRequested)
                {
                    float p = l != null && l.BatchTotal > 0
                        ? Mathf.Clamp01((float)l.BatchDone / l.BatchTotal) : 0f;
                    BootVeil.Progress(60 + Mathf.RoundToInt(p * 40f),
                        LvnNetworkStatus.IsOffline ? LvnOfflineText.Reconnecting : "загрузка…");
                    await Task.Yield();
                }
                if (ct.IsCancellationRequested) return;
                BootVeil.Status("");
                // ПЕРВЫЙ ВХОД НЕ ПОКАЗЫВАЕТ ЗАГРУЗКУ ВООБЩЕ. Впереди воронка —
                // вуаль не гаснет в меню, а превращается в имя продукта фейдом
                // и живёт, пока под ней качается и одевается первая сцена;
                // гасит её RevealFromLoadingAsync одним кроссфейдом в игру.
                if (_shell != null && _shell.HasPendingIntro)
                {
                    BootVeil.Brand(Application.productName);
                    Debug.Log($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms первый вход: брендовая вуаль до одетой сцены");
                }
                else
                {
                    // ВУАЛЬ ДЕРЖИТСЯ, ПОКА ПОЛОТНО НЕ ВСТАЛО (Илья 26.08: «при
                    // первом запуске бг чёрный»). Канвас меню — крупный кадр,
                    // его декод занимает полсекунды, и вуаль, снятая раньше,
                    // открывала пустую сцену: под ней чёрный. Ждём факт —
                    // но не дольше секунды с небольшим, иначе сорванная
                    // загрузка держала бы игрока в заставке.
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    while (Stage != null && !Stage.HasBackdrop && wait.ElapsedMilliseconds < LvnMenuStage.VeilWaitMs)
                        await System.Threading.Tasks.Task.Yield();
                    if (Stage != null && !Stage.HasBackdrop)
                        Debug.LogWarning($"[lvn-boot] полотно не встало за {wait.ElapsedMilliseconds}ms — снимаем вуаль без него");
                    await BootVeil.FadeOutAsync(LvnMenuStage.VeilFadeSeconds);
                }
                Debug.Log($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms veil handed off — app boot done");
                // Первый ЭКРАН, а не первый кадр: между запуском и этим местом
                // человек смотрит на загрузку и может уйти. Без этой ступени
                // воронка первой сессии начинается сразу с «начал главу», и
                // потери на загрузке выглядят так, будто игра никому не нужна.
                // Длительность здесь же: «долго грузилось» — самая частая
                // причина уйти, не начав.
                Lvn.Services.LvnAnalytics.Track("first_screen",
                    ("boot_ms", bootClock.ElapsedMilliseconds),
                    ("offline", LvnNetworkStatus.IsOffline));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                BootVeil.Hide();
            }
        }

        // ── chapter loading gate ────────────────────────────────────────────────
        // The shell's loading screen used to be decorative (ready = always true):
        // the script fetch, the state load and the first bg decode all happened
        // AFTER it faded — the player entered a black stage while the chapter
        // actually loaded. Now the screen kicks the real work and gates on it:
        // the script download plus the chapter's critical (required) assets via
        // the prioritized AssetScheduler; deferred assets keep streaming during
        // play. Offline every fetch fast-fails into the disk cache, so the gate
        // still completes — OfflinePolicy in PlayOneChapterAsync then decides
        // whether the chapter can actually play.
        private AssetScheduler _chapterSched;
        private Task _chapterScript = Task.CompletedTask;
        private LvnChapter _preparedChapter;

        private Func<bool> BeginChapterLoading(LvnChapter ch)
        {
            if (ch == null || _downloads == null) return () => true;
            _chapterScript = string.IsNullOrEmpty(ch.script_url)
                ? Task.CompletedTask
                : _assets.Loader.DownloadScriptCached(ch.script_url);
            _chapterSched = _downloads.BeginChapter(ch, destroyCancellationToken);
            _preparedChapter = ch;
            var script = _chapterScript;
            var sched = _chapterSched;
            LvnAsync.Fire(WatchChapterWarmAsync(ch, script, sched), "WatchChapterWarm");
            // A faulted script task still completes the gate — PlayOneChapterAsync
            // owns the error path (cache fallback / "unavailable offline").
            return () => script.IsCompleted && sched.RequiredReady;
        }

        // Timing telemetry for the loading gate: how long the script fetch and
        // the required-asset warm ACTUALLY took (per-asset costs are the
        // [lvn-perf] lines) — the number to shrink when "loading feels long".
        private static async Task WatchChapterWarmAsync(LvnChapter ch, Task script, AssetScheduler sched)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await script; } catch { /* the gate's error path reports it */ }
            Debug.Log($"[lvn-boot] warm {ch.id}: script +{sw.ElapsedMilliseconds}ms");
            while (!sched.RequiredReady && sw.ElapsedMilliseconds < 120_000)
                await Task.Delay(100);
            Debug.Log($"[lvn-boot] warm {ch.id}: required assets {sched.RequiredDone}/{sched.RequiredTotal} +{sw.ElapsedMilliseconds}ms");
        }

        // Progress for the loading bar: bytes when the manifest reports asset
        // sizes, else the required-count fraction (an empty/finished plan is 1).
        private float ChapterLoadProgress()
        {
            var s = _chapterSched;
            if (s == null || s.RequiredReady) return 1f;
            var p = s.Progress;
            if (p > 0f) return p;
            return s.RequiredTotal > 0 ? (float)s.RequiredDone / s.RequiredTotal : 0f;
        }

        // Charge a title's hub-entry cost (typically 1 energy for an expedition)
        // before it launches. Same store-retry flow as the per-chapter gate; free
        // when the title has no cost. Returns true if the player may enter.
        private async Task<bool> ChargeTitleEntryAsync(LvnTitle title)
        {
            var cost = title?.cost;
            if (cost == null || string.IsNullOrEmpty(cost.currency) || cost.amount <= 0) return true;
            // The entry was paid when the title was FIRST started — «Продолжить»
            // (or menu-exit + Play) must not charge the same entry again.
            if (LvnProgress.Reached(title) > 0) return true;

            return await ChargeEntryAsync(cost.currency, cost.amount,
                "title:" + title.id, "You need more to start this.");
        }

        /// <summary>
        /// ОБРЯД ОПЛАТЫ — единственный способ взять с игрока деньги за вход.
        ///
        /// <para>Списать; если не хватило — объяснить, чего и сколько, и
        /// предложить магазин; после магазина попробовать ещё раз; если и тогда
        /// нет — сказать об этом прямо. Порядок и формулировки были написаны
        /// дважды, для входа в новеллу и в главу, слово в слово, — и разошлись
        /// бы при первой же правке одного из них.</para>
        /// </summary>
        /// <param name="currency">чем платим</param>
        /// <param name="amount">сколько</param>
        /// <param name="reason">за что — уходит в кошелёк и в аналитику</param>
        /// <param name="fallbackMessage">объяснение, когда новелла своего не дала</param>
        private async Task<bool> ChargeEntryAsync(string currency, long amount,
                                                  string reason, string fallbackMessage)
        {
            if (string.IsNullOrEmpty(currency) || amount <= 0) return true; // бесплатно
            if (await Lvn.Services.LvnWallet.SpendAsync(currency, amount, reason)) return true;
            if (_shell == null) return false;

            var eco = _manifest?.economy;
            string title = eco?.gate_title ?? "Not enough energy";
            string msg = (eco?.gate_message ?? fallbackMessage) + RefillHint(currency);
            bool toStore = await _shell.ConfirmAsync(title, msg,
                eco?.gate_buy ?? "Store", eco?.gate_cancel ?? "Not now");
            if (!toStore) return false;

            await _shell.OpenPackShopAsync();
            await Lvn.Services.LvnWallet.RefreshAsync();
            if (await Lvn.Services.LvnWallet.SpendAsync(currency, amount, reason)) return true;

            await _shell.AlertAsync(eco?.gate_denied ?? title, msg);
            return false;
        }

        // Populate the CG gallery from every title's `gallery` (unlock state from
        // LvnGalleryStore), then open it. No items anywhere → the screen keeps its
        // built-in demo fallback.
        private Task OpenGalleryForRealAsync()
        {
            if (_shell?.Gallery != null && _manifest?.titles != null)
            {
                var entries = new System.Collections.Generic.List<CgGalleryScreen.Entry>();
                foreach (var t in _manifest.titles)
                    if (t?.gallery != null)
                        foreach (var g in t.gallery)
                            entries.Add(new CgGalleryScreen.Entry
                            {
                                Url = g.url,
                                Caption = g.name ?? g.id,
                                Unlocked = Lvn.UI.LvnGalleryStore.IsUnlocked(t.id, g.id),
                            });
                if (entries.Count > 0) _shell.Gallery.SetEntries(entries);
            }
            return _shell.OpenGalleryAsync();
        }

        // "⚡ +1 через 1 ч 20 мин" — the regen countdown for the gate popup, from the
        // wallet's computed refill state. Empty when the currency isn't regenerating.
        private static string RefillHint(string currency)
        {
            if (!Lvn.Services.LvnWallet.Regen.TryGetValue(currency, out var r) || r.NextRefillUnix <= 0) return "";
            long rem = r.NextRefillUnix - System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (rem <= 0) return "";
            long h = rem / 3600, m = (rem % 3600) / 60;
            return "\n\n+1 энергия через " + (h > 0 ? h + " ч " + m + " мин" : m + " мин");
        }

        private async Task OpenStoreFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try { await _shell.OpenPackShopAsync(); } // ONE store everywhere (the KR rule)
            finally { ctx.Resume(); }
        }

        private async Task OpenSettingsFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try { await _shell.OpenSettingsAsync(); }
            finally { ctx.Resume(); }
        }

        // The in-story sheet as CONTENT of the stage's shared window: the
        // dialogue fades out, the same-skinned frame fades in with the
        // wardrobe inside — one panel, native transitions (no overlay pop).
        private WardrobeSheet _storySheet;
        private int _storyActorSwitchGeneration;

        private async Task OpenWardrobeFromScriptAsync(string entity, bool full, Lvn.ILvnOpContext ctx)
        {
            try
            {
                // ONE wardrobe, one shell: mode=full historically opened a
                // separate fullscreen screen — it's gone; every path is the
                // sheet over the stage canvas now.
                await ShowStorySheetAsync(entity, onlySeen: false);
                _ = full; // accepted and ignored — deprecated authoring flag
            }
            finally { ctx.Resume(); }
        }

        // The ALWAYS-OPEN wardrobe (quick-menu «Гардероб»): the exact story-
        // moment experience — the hero on the current scene's background, the
        // same sheet — but the items are the player's COLLECTION: outfits the
        // story staged or offered along the way, plus everything bought.
        // Экран входа по просьбе сценария. Пропускаем, если игрок уже вошёл:
        // повторное «представьтесь» посреди главы читается как сбой.
        private async Task AuthFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try
            {
                var auth = _shell?.Auth;
                if (auth != null && !Lvn.UI.LvnPrefs.SeenWelcome)
                {
                    var nick = await auth.AskAsync(destroyCancellationToken);
                    Lvn.UI.LvnPrefs.SeenWelcome = true;
                    if (!string.IsNullOrEmpty(nick))
                    {
                        _playerName = nick;
                        Lvn.UI.LvnPrefs.PlayerName = nick;
                    }
                }
            }
            catch (OperationCanceledException) { /* приложение закрывают — история всё равно отпускается */ }
            finally { ctx.Resume(); }
        }

        // Окно в Режиссёра: своей копии режима у приложения больше нет.
        private bool _chapterPlaying => Lvn.UI.LvnScreenDirector.Current.InChapter;





        // Системный запрос разрешения на уведомления, вызванный сценой
        // (`ext push_ask`). История ждёт ровно столько, сколько открыт
        // платформенный диалог, и продолжается при ЛЮБОМ ответе — сцена
        // не должна знать, согласился игрок или нет (спросить дважды всё
        // равно нельзя, а наказание отказавшему читалось бы как вымогание).
        private async Task PushAskFromScriptAsync(Lvn.ILvnOpContext ctx)
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Android 13+: POST_NOTIFICATIONS — рантайм-разрешение. На более
                // старых версиях его нет в системе — Request тихо не делает ничего,
                // а уведомления и так разрешены по умолчанию.
                const string perm = "android.permission.POST_NOTIFICATIONS";
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(perm))
                {
                    bool done = false;
                    var cb = new UnityEngine.Android.PermissionCallbacks();
                    cb.PermissionGranted += _ => done = true;
                    cb.PermissionDenied += _ => done = true;
                    UnityEngine.Android.Permission.RequestUserPermission(perm, cb);
                    // Страховка от платформ, где колбэк не приходит: ждём ответ,
                    // но не дольше минуты — история важнее диалога.
                    float deadline = Time.realtimeSinceStartup + 60f;
                    while (!done && Time.realtimeSinceStartup < deadline)
                        await Task.Yield();
                }
#else
                // iOS требует пакет Mobile Notifications — подключим, когда
                // появится iOS-сборка; в редакторе и на десктопе просить нечего.
                Debug.Log("[novelapp] push_ask: платформа без запроса — пропускаю");
                await Task.CompletedTask;
#endif
            }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] push_ask: {ex.Message}"); }
            finally { ctx.Resume(); }
        }



        private static bool IsFirstChapter(LvnTitle title, LvnChapter chapter)
        {
            if (title?.seasons == null || chapter == null) return false;
            foreach (var se in title.seasons)
                if (se?.chapters != null)
                    foreach (var c in se.chapters)
                        if (c != null)
                            return c.id == chapter.id; // the manifest's first listed chapter
            return false;
        }






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

        private async Task SafeBootPrefetch(LvnManifest manifest, bool online)
        {
            // Online: verify + download the boot set. Offline: warm only what's
            // already on disk (no network), so a cached install still shows its art.
            try { await _downloads.BootPrefetchAsync(manifest, online, default); }
            catch { /* best-effort — missing boot art is non-fatal */ }
        }

        // Probe the server's /healthz with a hard 3s deadline. Token-based, because
        // UnityWebRequest.timeout doesn't reliably interrupt a DNS/TLS stall — the
        // difference between an instant offline fallback and a ~30s boot hang.
        private async Task<bool> ProbeOnlineAsync()
        {
            try
            {
                using var probe = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await _assets.Loader.HealthzAsync("/healthz", probe.Token);
            }
            catch { return false; }
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
                PlayerPrefs.SetString(ManifestCacheKey, Newtonsoft.Json.JsonConvert.SerializeObject(m));
                PlayerPrefs.Save();
            }
            catch { /* cache write best-effort */ }
        }

        private static LvnManifest LoadCachedManifest()
        {
            try
            {
                var json = PlayerPrefs.GetString(ManifestCacheKey, null);
                return string.IsNullOrEmpty(json)
                    ? null
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            }
            catch { return null; }
        }





        // Charge the chapter-entry currency (typically the regenerating "energy")
        // before a fresh chapter loads. Returns true when the player may enter:
        // the gate is disabled, the chapter is free, the spend succeeded, or a
        // store purchase covered it. On a hard refusal (no funds and no/failed
        // purchase) shows a popup and returns false, dropping back to the carousel.
        private async Task<bool> ChargeChapterEntryAsync(LvnChapter chapter)
        {
            var eco = _manifest?.economy;
            var currency = eco?.chapter_currency;
            int cost = eco?.chapter_cost ?? 1;
            if (string.IsNullOrEmpty(currency) || cost <= 0) return true; // gate off
            if (eco.free_chapters != null && chapter != null && eco.free_chapters.Contains(chapter.id))
                return true; // this chapter is on the house

            return await ChargeEntryAsync(currency, cost,
                "chapter:" + chapter?.id, "You need more to open this chapter.");
        }


        // Stream one chapter's script and run it through the VnStage, driving the
        // HUD until it ends. Returns the chapter that actually FINISHED (it can
        // differ from the requested one — a cross-chapter save load switches the
        // stage mid-play), or null when the player left mid-chapter.
        // Адреса, о промахе которых уже отчитались: одна пропавшая картинка в
        // цикле показа даёт сотни попыток, а событие нужно одно.
        private static readonly HashSet<string> _reportedAssetFails = new HashSet<string>();

        private static void OnAssetFailed(string url, long code)
        {
            if (string.IsNullOrEmpty(url)) return;
            lock (_reportedAssetFails)
            {
                if (_reportedAssetFails.Count > 200) _reportedAssetFails.Clear(); // без роста без предела
                if (!_reportedAssetFails.Add(url)) return;
            }
            Lvn.Services.LvnAnalytics.Track("asset_fail", ("asset", url), ("code", code));
        }

        // Метки, о которых уже отчитались в этой главе. Цикл («спросить ещё
        // раз») проходит одну и ту же метку многократно, а для воронки важен
        // ФАКТ «дошёл», а не счётчик оборотов.
        private static readonly HashSet<string> _reachedLabels = new HashSet<string>();

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

        private static void OnLabelReached(string label, int at)
        {
            if (string.IsNullOrEmpty(label)) return;
            lock (_reachedLabels)
            {
                if (_reachedLabels.Count > 500) _reachedLabels.Clear(); // без роста без предела
                if (!_reachedLabels.Add(label)) return;
            }
            Lvn.Services.LvnAnalytics.Track("label_reach", ("label", label), ("at", at));
            // Та же позиция нужна отзыву: «тут баг» без места в сценарии
            // невоспроизводим, а сам игрок место назвать не может.
            Lvn.Services.LvnFeedback.CurrentLabel = label;
            Lvn.Services.LvnFeedback.CurrentAt = at;
        }

        private static void OnChoiceShown(int written, int shown, int at)
            => Lvn.Services.LvnAnalytics.Track("choice_shown",
                ("written", written), ("shown", shown), ("at", at));

        private static void OnChoicePicked(int index, string text, float seconds, int at)
            => Lvn.Services.LvnAnalytics.Track("choice_pick",
                ("option", index), ("text", text), ("seconds", System.Math.Round(seconds, 1)), ("at", at));

        /// <summary>
        /// Отдаёт аналитике операции, которых рантайм не знает, и обнуляет
        /// счётчик. Копится он всю главу (LvnPlayer.UnclaimedOps), а смысл имеет
        /// только собранным: «в этой главе трижды встретился неизвестный op» —
        /// это либо расхождение рантаймов, либо хост-оп, который забыли
        /// зарегистрировать, и узнавать об этом надо не от игрока.
        /// </summary>
        private static void FlushUnknownOps(LvnTitle title, LvnChapter chapter)
        {
            var ops = LvnPlayer.UnclaimedOps;
            if (ops == null || ops.Count == 0) return;
            Lvn.Services.LvnAnalytics.Track("unknown_op",
                ("ops", ops), ("title", title?.id), ("chapter", chapter?.id));
            LvnPlayer.ResetOpDiagnostics();
        }

        private async Task<LvnChapter> PlayOneChapterAsync(LvnTitle title, LvnChapter chapter, string playerName, bool novelFreshStart = false)
        {
            if (Stage == null || chapter == null || string.IsNullOrEmpty(chapter.script_url))
            {
                await Task.Delay(400);
                return null;
            }

            // Clean the stage at the START too — not just on the previous chapter's
            // end — so a leftover actor/animation never lingers while this chapter's
            // script is still downloading.
            Stage.ClearStage();

            // Per-title theme: engine defaults → global manifest.ui → this title's ui.
            // Rebuilt fresh each entry so a previous title's look never leaks in.
            var theme = VnThemeBuilder.From(_globalUi, new VnTheme());
            if (title?.ui != null) theme = VnThemeBuilder.From(title.ui, theme);
            Stage.ApplyTheme(theme);

            // Offline decision layer (ported from the Liminal client): decide how
            // to enter the chapter from connectivity + what's on disk. A local
            // bundle reports everything cached/reachable, so it plays instantly;
            // an online client degrades gracefully and never hangs.
            bool online = _assets.Loader.IsLocal || !LvnNetworkStatus.IsOffline;
            var readiness = OfflinePolicy.ComputeReadiness(
                _assets.Loader.IsScriptCached(chapter.script_url),
                chapter.assets,
                _assets.Loader.IsAssetCached);
            var plan = ChapterEntryPlan.From(online, in readiness);
            if (!plan.CanPlay)
            {
                Debug.LogWarning($"[novelapp] chapter '{chapter.id}' unavailable offline (script not cached)");
                await Task.Delay(300);
                return null;
            }

            string json;
            try { json = await _assets.Loader.DownloadScriptCached(chapter.script_url); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] script fetch failed: {ex.Message}"); return null; }
            if (string.IsNullOrEmpty(json)) { Debug.LogWarning($"[novelapp] no script for '{chapter.id}'"); return null; }

            _currentChapter = chapter;
            _currentTitle = title;
            _playerName = playerName;
            _currentScriptJson = json;
            Stage.Strings = await LoadCatalogAsync(chapter.script_url); // localization (null → inline text)
            // Carry this title's persisted stats into the chapter (relationships, route,
            // memory flags…). The imported global defaults are `default:true`, so they
            // don't overwrite these; a fresh game starts empty. The store is local-first
            // (offline-safe) and, when a server is configured, syncs through /v1/state.
            Stage.SeedVars = await LoadScopedVarsAsync(title?.id);

            // The genre-standard restart semantics: picking a chapter from the
            // picker resets the variables to what they were when that chapter was
            // FIRST entered — stats from the future must not leak into the past
            // and mis-gate its choices. The live state store rolls back with it,
            // so a later stat sync doesn't resurrect the discarded future.
            bool restart = LvnProgress.TakeRestart(title?.id, chapter.id);
            if (restart)
            {
                Stage.SeedVars = LvnProgress.Checkpoint(title?.id, chapter.id)
                                 ?? new Newtonsoft.Json.Linq.JObject();
                // Global (cross-novel) stats must NOT roll back with a per-chapter
                // restart — overlay the CURRENT global stats over the checkpoint.
                // Local first: a kill during the network sync must not leave the
                // old autosave alive with the restart flag already consumed.
                LvnSaveStore.Delete(title?.id, LvnSaveStore.AutoSlot);
                var curGlobal = await _state.LoadVarsAsync(GlobalScopeId, default);
                if (curGlobal != null && curGlobal.Count > 0) Stage.SeedVars[GlobalVar] = curGlobal;
                await SaveScopedVarsAsync(title?.id, Stage.SeedVars);
                Debug.Log($"[novelapp] restarting '{chapter.id}' from its entry checkpoint");
            }

            // Resume where the player actually was: a mid-chapter autosave for THIS
            // script (written on choices/every few lines/app pause) beats replaying
            // the chapter from the top. A finished chapter's autosave was deleted on
            // OnEnd, so replays start clean.
            var autosave = LvnSaveStore.Get(title?.id, LvnSaveStore.AutoSlot);
            bool resuming = !restart && autosave?.Snap != null
                            && autosave.Snap.ScriptUrl == chapter.script_url
                            && !autosave.Snap.Finished;

            // Device-side wardrobe equips (the hub sheet has no live Player to
            // write through) land in the story vars HERE: every wardrobe slot
            // bound to a story var seeds the equipped value on a fresh entry, so
            // template-driven axes ({Wardrobe.mainCh_Clothes}) show the outfit
            // the player picked between sessions. Resumes keep the snapshot's
            // own state — the story's mid-chapter forces stay authoritative.
            if (!resuming && _manifest?.sprites != null && Stage.SeedVars != null)
            {
                foreach (var kv in _manifest.sprites)
                {
                    var wb = kv.Value?.wardrobe;
                    if (wb == null) continue;
                    var worn = Lvn.UI.LvnWardrobe.Equipped(kv.Key);
                    if (worn.Count == 0) continue;
                    foreach (var slot in wb)
                        if (!string.IsNullOrEmpty(slot.Value?.storyVar)
                            && worn.TryGetValue(slot.Key, out var val) && !string.IsNullOrEmpty(val))
                            SetVarPath(Stage.SeedVars, slot.Value.storyVar, val);
                }
            }

            // A FRESH entry (chapter transition, picker restart, first launch) is
            // the moment the entry checkpoint captures; a mid-chapter resume must
            // NOT overwrite it — and neither may a REPLAY: the checkpoint is the
            // vars of the FIRST entry ever, the restart anchor. Overwriting it
            // with a later playthrough's stats would corrupt restarts forever.
            if (!resuming && LvnProgress.Checkpoint(title?.id, chapter.id) == null)
                LvnProgress.SaveCheckpoint(title?.id, chapter.id, Stage.SeedVars);

            Stage.SetSaveContext(title?.id, chapter.id, chapter.script_url);
            Stage.Gallery = title?.gallery;
            // The first line holds until the entry choreography (loader reveal,
            // plus the chapter-title card on fresh entries) finishes — the stage
            // dresses itself silently underneath. A RESUME holds too (it skips
            // only the title card): without the gate the first line typed — with
            // its keystroke sound — under the still-opaque loader, and the reveal
            // faded into a scene already mid-sentence.
            var entryDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stage.EntryGate = entryDone.Task;
            Stage.Play(json, warmIntroSpine: !resuming); // resume restores below — don't run/warm the intro
            if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                Stage.Player.Vars["player"] = playerName;

            // Title-level variable declarations (title.vars_url): ONE block per
            // game instead of a 250-op boilerplate at the top of every chapter.
            // Fresh entry: chapter-scoped keys reset to their defaults; then both
            // scopes fill only what is still unset (progress always wins).
            var titleVars = await LoadTitleVarsAsync(title);
            if (titleVars != null && Stage.Player != null && !resuming)
            {
                Stage.Player.ResetScope((titleVars.chapter ?? new Newtonsoft.Json.Linq.JObject())
                    .Properties().Select(p => p.Name).ToList());
                Stage.Player.ApplyDefaults(titleVars.game);
                Stage.Player.ApplyDefaults(titleVars.chapter);
            }

            if (!resuming)
            {
                // The entry IS the purchase receipt: write the autosave NOW, so a
                // crash in the first lines never re-charges this chapter (and the
                // player lands back at its top, not at the carousel).
                Stage.AutosaveNow();
            }
            if (resuming)
            {
                Debug.Log($"[novelapp] resuming '{chapter.id}' from autosave (@{autosave.Snap.Index})");
                // The snapshot carries the GLOBAL stats as they were at save time —
                // another novel may have moved them since. Load the live ones FIRST:
                // the overlay below then runs before any of the restore's async
                // continuations, so the resumed beat's conditions read fresh stats.
                var freshGlobal = await _state.LoadVarsAsync(GlobalScopeId, default);
                Stage.RestoreSnapshot(autosave.Snap);
                if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                    Stage.Player.Vars["player"] = playerName;
                if (freshGlobal != null && freshGlobal.Count > 0 && Stage.Player != null)
                    Stage.Player.Vars[GlobalVar] = freshGlobal;
                // A resume keeps the snapshot's own state; the declaration only
                // fills keys the snapshot never had (e.g. vars added after the
                // save was written) — never resets chapter scope mid-chapter.
                if (titleVars != null && Stage.Player != null)
                {
                    Stage.Player.ApplyDefaults(titleVars.game);
                    Stage.Player.ApplyDefaults(titleVars.chapter);
                }
            }

            await RevealChapterEntryAsync(title, chapter, resuming, restart, novelFreshStart, entryDone);

            // Drive the HUD percent until the chapter ends — or the player asks
            // out (the quick menu's Exit; position already autosaved, so the
            // carousel's Continue leads straight back to this line).
            // Task.Yield can't throw — the real exit-on-teardown is the token
            // check (a destroyed host must not keep a zombie progress loop).
            _shell.Hud.SetStats(title?.stats, key => Stage.Player?.GetVar(key));
            while (Stage.Player != null && !Stage.Player.Finished && !Stage.ExitRequested
                   && !destroyCancellationToken.IsCancellationRequested)
            {
                _shell.Hud.SetProgress(Stage.Player.ProgressIndex, Stage.Player.ProgressTotal);
                _shell.TopBar?.SetProgress(Stage.Player.ProgressIndex, Stage.Player.ProgressTotal);
                await Task.Yield();
            }
            bool exited = Stage.ExitRequested;
            Stage.ClearExitRequest();
            // Вышли из главы — кадр ПЕРЕХОДИТ меню, а не стирается: полотно
            // меняется кроссфейдом, героиня остаётся стоять в том наряде и с
            // той эмоцией, с какими кончилась глава.
            if (exited) HandOverToMenu();
            // Persist the chapter's ending state so the next chapter (and the next
            // session) resume with the same stats — whether it finished or the player
            // left mid-chapter (the loop also breaks on cancellation).
            // The owner may have CHANGED under us (a cross-title save load) —
            // the finished chapter's vars belong to the title actually playing.
            var ownerId = _currentTitle?.id ?? title?.id;
            // Сейв статов НЕ держит финал: локальная запись внутри синхронна
            // (до первого await), а сетевые PUT — до 8 с каждый с ретраями —
            // шли ПЕРЕД экраном «Конец главы» и читались как зависание на
            // пустой сцене (живой репорт «при завершении главы какой-то лаг»).
            // Тот же fire-and-forget уже канон на паузе приложения.
            if (Stage.Player != null)
                LvnAsync.Fire(SaveScopedVarsAsync(ownerId, VarsToJObject(Stage.Player.Vars)),
                    "SaveScopedVars@chapterEnd");
            _shell.Hud.SetProgress(1, 1);
            _shell.TopBar?.SetProgress(1, 1);
            _shell.Hud.SetStats(null, null);
            // The chapter that actually played to the end — a cross-chapter save
            // load may have switched the stage away from the requested one.
            bool finished = Stage.Player != null && Stage.Player.Finished;
            var played = _currentChapter ?? chapter;
            _currentChapter = null;
            _currentTitle = null;
            // Free the finished chapter's decoded art (a chapter can hold dozens of
            // full-res RGBA sprites). Anything the MENU still shows is pinned:
            // covers and loading backdrops often reuse in-chapter bg files, and
            // destroying a sprite the carousel still references leaves white
            // cards. The disk cache is intact so the next entry re-decodes fast.
            var pinned = MenuArtUrls();
            // Уничтожение десятков 2K-текстур — один тяжёлый кадр. Синхронно он
            // стоял ПЕРЕД экраном «Конец главы» и складывался с сетевым сейвом в
            // видимый «лаг на финале». Три кадра спустя экран уже нарисован —
            // хитч случается под ним, а не перед ним.
            LvnAsync.Fire(UnloadChapterArtSoonAsync(pinned), "UnloadChapterArt");
            return finished ? played : null;
        }

        // Отложенная выгрузка арта доигранной главы: пины меню держат ORIGINAL
        // urls, кэш может держать @2k-варианты. Пара кадров — чтобы экран
        // «Конец главы» успел отрисоваться; следующая глава начинает грузить
        // свой арт много позже (сначала едет скрипт).
        private async Task UnloadChapterArtSoonAsync(HashSet<string> pinned)
        {
            for (int i = 0; i < 3; i++) await Task.Yield();
            // /sprites/ здесь обязателен: послойный облик героини (~240 МБ
            // декода) не матчился и переживал главу целиком.
            _assets.Loader.UnloadWhere(u =>
                (u.Contains("/art/") || u.Contains("/bg/") || u.Contains("/sprites/"))
                && !pinned.Contains(Lvn.Content.DownloadPolicy.StripVariant(u)));
            // Диск убирается тем же тактом (в пуле потоков): мёртвые версии —
            // всегда, над квотой — давнее. Общий арт глав живёт одним файлом
            // и не удаляется, пока его знает хоть одна глава манифеста.
            await SweepDiskCacheAsync();
        }

        // Квота дискового кэша ассетов. Позже — настройка; 500 МБ покрывает
        // «скачать всё» Time Romance с запасом и не даёт кэшу расти вечно.
        private const long DiskCacheQuotaBytes = 500L << 20;







        // The cross-novel player-stat namespace. Stats under the `global` var
        // (scripts: `set/inc key="global.<stat>"`, read `global.<stat>`) persist to
        // a per-player state blob shared by EVERY novel, so they accumulate across
        // titles and one novel can read what another left behind. Ordinary vars stay
        // scoped to their title.
        private const string GlobalVar = "global";
        private const string GlobalScopeId = "__global";









        // Mobile: persist stats when the app is backgrounded / quit mid-chapter.
        // Fire-and-forget — the store writes its LOCAL cache synchronously before the
        // first await, so stats are safe even if the process is suspended immediately.
        // Desktop/editor: closing the window must save exactly like a mobile
        // background — otherwise the last lines and unsynced vars are lost.
        private void OnApplicationQuit() => OnApplicationPause(true);


        private void OnApplicationPause(bool paused)
        {
            if (paused && _state != null && Stage?.Player != null && _currentTitle != null)
                LvnAsync.Fire(SaveScopedVarsAsync(_currentTitle.id, VarsToJObject(Stage.Player.Vars)), "SaveScopedVars");
            if (paused) SyncProgressVault();
            // Position too, not just stats — so a suspended app resumes on the same
            // line (the autosave slot; SaveToSlot is synchronous PlayerPrefs).
            if (paused) Stage?.AutosaveNow();
        }

        // Server content changed: refresh the version index, re-apply the manifest
        // (carousel rebuilds), and hot-reload the open chapter if its script moved.
        private async void OnContentChanged()
        {
            Debug.Log("[novelapp] content changed — reloading");
            try { await _assets.WarmVersionsAsync(); } catch { /* offline */ }

            LvnManifest manifest;
            try { manifest = await FetchManifestAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] live manifest fetch failed: {ex.Message}"); return; }
            CacheManifest(manifest); // keep the offline copy fresh on every live update
            // Pull the changed boot-set bytes and re-warm replaced covers BEFORE the
            // carousel rebuilds — otherwise it re-renders from the stale in-memory
            // sprites and a cover swap on the server never shows up.
            try { await _downloads.MenuRefreshAsync(manifest, default); }
            catch { /* best-effort; never blocks the live update */ }
            _shell?.ApplyLiveUpdate(manifest);
            _storySheet?.SetManifest(manifest); // the in-story wardrobe follows live edits too
            _globalUi = manifest.ui;
            _manifest = manifest; // cross-chapter routing follows the live manifest
            ApplyMenuStaging(manifest);
            _assets.Set3DSetCatalog(manifest.sets3d);
            if (Stage != null)
            {
                Stage.Catalog = new SpriteCatalog(manifest.sprites);
                // Re-theme live — rebuilt fresh from the NEW manifest: engine
                // defaults → global ui → the playing title's ui override (matched
                // by id in the new manifest, so per-title edits take effect). Safe
                // mid-line: VnStage.ApplyTheme restores the visible line/choices.
                var theme = VnThemeBuilder.From(manifest.ui, new VnTheme());
                LvnTitle liveTitle = null;
                if (_currentTitle != null && manifest.titles != null)
                    liveTitle = manifest.titles.Find(t => t != null && t.id == _currentTitle.id);
                if (liveTitle?.ui != null) theme = VnThemeBuilder.From(liveTitle.ui, theme);
                Stage.ApplyTheme(theme);
            }

            if (_currentChapter == null || Stage == null || Stage.Player == null || Stage.Player.Finished)
                return;

            // Fetch the script FRESH (not the version-pinned disk cache, which can
            // hand back the old text when reacting to a live edit — the whole point
            // here is to apply what just changed). The disk cache is refreshed in
            // the background so an offline replay of the new version still works.
            string json;
            try { json = await _assets.Loader.DownloadScriptText(_currentChapter.script_url); }
            catch { return; }
            if (string.IsNullOrEmpty(json)) return;
            if (json == _currentScriptJson)
            {
                // The script didn't change — only assets did (a replaced sprite or
                // background). Re-apply the visible stage in place so the new art shows
                // live, without restarting the chapter. The version index was just
                // re-warmed, so each sprite reloads under its new content hash.
                if (Stage.Player != null && !Stage.Player.Finished)
                    Stage.Player.ReplayVisuals(Stage.Player.Index + 1);
                return;
            }
            _assets.Loader.RefreshScriptInBackground(_currentChapter.script_url);

            _currentScriptJson = json;
            // A non-structural edit (reworded line, tweaked emotion/position) keeps
            // the chapter playing exactly where it is; only a changed command
            // structure forces a restart from the top.
            if (Stage.TryHotSwap(json))
            {
                Debug.Log($"[novelapp] hot-swapped chapter '{_currentChapter.id}' in place (kept position)");
            }
            else
            {
                Stage.Play(json);
                if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                    Stage.Player.Vars["player"] = _playerName;
                Debug.Log($"[novelapp] reloaded chapter '{_currentChapter.id}' (structure changed — restarted)");
            }
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
