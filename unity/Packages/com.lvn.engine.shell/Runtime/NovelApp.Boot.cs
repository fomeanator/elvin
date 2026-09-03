using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ПОДЪЁМ ПРИЛОЖЕНИЯ — порядок первых секунд и то, что происходит на
    /// границах жизни: свернули, вернули, закрыли.
    ///
    /// <para>Порядок здесь не формальность, а список выученных уроков. Тема
    /// ложится на панель ДО вуали: панель без темы не имеет шрифта, и каждая
    /// надпись на вуали рисуется как ничто — «чёрный экран без текста». Первый
    /// кадр рисуется ДО первого сетевого запроса, иначе устройство сидит на
    /// голом чёрном, пока идёт подъём. Между вуалью и тяжёлой работой стоят два
    /// пропуска кадра: на медленных телефонах рендер первого кадра голодал, и
    /// первый видимый процент был уже тридцатым.</para>
    ///
    /// <para>Здесь же телеметрия подъёма: один секундомер и метка на фазу —
    /// <c>[lvn-boot]</c> читается как профиль запуска, и всё, что в нём растёт,
    /// это регрессия, которую видно до жалоб.</para>
    ///
    /// <para>Отдельным домом, потому что подъём — единственная часть, которая
    /// исполняется РОВНО ОДИН РАЗ и в строгом порядке. Внутри
    /// двухтысячестрочного класса этот порядок терялся между функциями, которые
    /// вызываются когда угодно.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        /// <summary>
        /// ПРИЛОЖЕНИЕ ЗАКРЫВАЮТ — снятая КОПИЯ ответа, а не сам вопрос.
        ///
        /// <para><c>destroyCancellationToken</c> — свойство НА КОМПОНЕНТЕ.
        /// Пока компонент жив, оно отдаёт токен; после <c>Destroy</c>
        /// обращение к нему бросает <c>MissingReferenceException</c>. Беда в
        /// том, что читают его обычно ИМЕННО ТАМ, где проверяют «а не снесли ли
        /// нас» — то есть после ожидания, — и проверка на мёртвом объекте
        /// падает вместо того, чтобы ответить «да, снесли».</para>
        ///
        /// <para>Исключение вылетает в продолжении после <c>await</c>, ловить
        /// его некому, и оно уходит в лог. В игре это тихо, а в прогоне
        /// валится СЛЕДУЮЩИЙ тест — на чужом сообщении, у себя в SetUp. Так
        /// краснота и простояла с 02:44 до 13:45, пока её не нашёл бисект.</para>
        ///
        /// <para>Сама структура токена уничтожение переживает и честно
        /// отвечает «отменено». Поэтому копия снимается ОДИН раз, до первого
        /// ожидания, и все проверки смотрят на неё.</para>
        /// </summary>
        private System.Threading.CancellationToken _quitting;

        // Точка входа Unity — единственный законный `async void` здесь. Но
        // упавший бут молчал: исключение уходило в никуда, а игрок оставался
        // перед вуалью, которая «просто не догружается». Теперь падение видно и
        // в логе, и на самой вуали.
        private async void Start()
        {
            _quitting = destroyCancellationToken;   // до первого await, пока живы
            // Хранилища оболочки объявляют себя ЗАБВЕНИЮ: движок их не видит
            // (Engine не знает про Shell), а забывать их надо вместе со всеми.
            Lvn.UI.LvnForget.Register("прогресс", LvnProgress.ResetTitle, null);
            Lvn.UI.LvnForget.Register("сейф прогресса", null, ProgressVault.Forget);
            Lvn.UI.LvnForget.Register("идентификатор", null, ForgetUserId);
            // СЕРВИСНЫЕ ДОМА ТОЖЕ ХРАНЯТ ИГРОКА, и обряд их не знал.
            // Самое острое — очереди: долговечная очередь событий переживала
            // забвение и уходила на сервер УЖЕ ПОСЛЕ него, а обряд честно
            // рапортовал об успехе. Плюс баланс с меткой владельца, источник
            // установки и группы экспериментов.
            Lvn.UI.LvnForget.Register("кошелёк", null, Lvn.Services.LvnWallet.Forget);
            Lvn.UI.LvnForget.Register("очередь событий", null, Lvn.Services.LvnAnalytics.Forget);
            Lvn.UI.LvnForget.Register("очередь логов", null, Lvn.Services.LvnLogShip.Forget);
            Lvn.UI.LvnForget.Register("источник установки", null, Lvn.Services.LvnAttribution.Forget);
            Lvn.UI.LvnForget.Register("группы экспериментов", null, Lvn.Services.LvnExperiments.Forget);

            try { await StartAsync(); }
            catch (Exception e)
            {
                LvnLog.Error("[lvn-boot] бут сорвался: " + e);
                try { BootVeil.Status(LvnWords.Of("boot.failed", "startup failed — check the log")); }
                catch { /* вуали уже нет: сообщать некуда, лог уже написан */ }
            }
        }

        /// <summary>Версия контента на момент начала запуска — точка отсчёта
        /// для ContentSync (см. его Baseline).</summary>
        private Task<string> _syncBaseline;

        private async Task StartAsync()
        {
            ConfigureFrameRate();

            // Boot telemetry: one stopwatch, a mark per phase — `adb logcat -s
            // Unity | grep lvn-boot` (or the editor console) reads as a boot
            // profile. Anything that grows here is a regression to hunt.
            var bootClock = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string phase) => LvnLog.Trace($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms {phase}");

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
            // ЗАСТАВКА, А НЕ ЗАГРУЗКА (решение Ильи 01.09). Вместо процентов —
            // имя игры на тёмном, две секунды и ровный уход. Точное имя знает
            // каталог (ui.browse.title), но он ещё не прочитан, а первый кадр
            // ждать не должен: берём имя сборки и поправим текст, когда каталог
            // ляжет, — это случится задолго до конца проявления.
            BootVeil.Splash(Application.productName);
            Mark("veil up (first paint)");
            // СТУПЕНЬ КАЧЕСТВА — ДО ПЕРВОГО ПРОГРЕВА, А НЕ ПОСЛЕ.
            //
            // Выбор игрока (@2k/@1440/@1k) применялся в WireQuickMenu, то есть
            // ПОСЛЕ PrepareStage, который уже поставил греться полотно витрины
            // и куклу героини. Обещание «синхронизируем до первой загрузки»
            // было ложным: первая загрузка к этому мигу уже случилась — по
            // СОВЕТУ УСТРОЙСТВА, а не по выбору игрока.
            //
            // Цена видна в живом трейсе холодного входа (04.09): прогрев тянул
            // слои героини как «@1440», через четверть секунды показ просил
            // «@2k» — и качал их заново, крупнее и дороже. Четыре слоя, четыре
            // лишних расшифровки; игрок всё это время смотрел на силуэт.
            //
            // Ответ ни от чего не зависит: настройка лежит в памяти устройства,
            // совет считается по экрану. Спрашивать его позже было нечем
            // оправдано.
            DownloadPolicy.PreferredSuffix = DownloadPolicy.SuffixFor(EffectiveArtQuality());
            // Штамп сборки: время последней компиляции каждой Lvn-сборки.
            // Отвечает на вечный вопрос «а этот прогон вообще на новом коде?»
            // без раскопок в Library/ScriptAssemblies.
            LvnLog.Info(Lvn.LvnBuildStamp.Line(
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
                LvnLog.Info($"[lvn-app] server override (dev): {ServerUrl}");
            }
            else
            {
                // CS-1.6-style server pick, over the veil: unchecked (default),
                // the known servers race a /healthz ping and the first live one
                // wins — invisible unless nothing answers in time. Checked
                // (persisted), a small browser lists them plus a free-text field
                // for the player's own host, and waits for an explicit Connect.
                ServerUrl = await ServerSelectScreen.ResolveAsync(ServerUrl, KnownServers, _quitting);
                LvnLog.Info($"[lvn-app] server resolved: {ServerUrl}");
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
            // ТОЧКА ОТСЧЁТА ДЛЯ ЖИВОГО КОНТЕНТА — снимается ДО того, как забран
            // контент, и потому отвечает на вопрос «сменился ли он по дороге».
            // Семьдесят девять байт, рядом с тремя другими рейсами. См.
            // ContentSync.Baseline: без неё первый опрос перезагружал главу на
            // каждом запуске.
            _syncBaseline = Lvn.Content.ContentSync.PeekVersionAsync(_assets.Loader);
            BootVeil.Progress(10, LvnWords.Of("boot.connecting", "connecting…"));

            // ЕСТЬ ВЧЕРАШНИЙ КАТАЛОГ — НЕ ЖДЁМ СЕТЬ ВООБЩЕ.
            //
            // Каждый из трёх рейсов отвечает на свой вопрос, но ни один ответ не
            // нужен, чтобы НАРИСОВАТЬ витрину: она была нарисована в прошлый
            // раз, и кэш лежит на диске. Прежде запуск при живой сети ждал
            // круговой рейс (а проба ещё и держала свои три секунды) — ради
            // знания, которое приезжает следом и доводится живым обновлением.
            //
            // Проба остаётся: она пиннит признак офлайна для всех последующих
            // запросов и гонит серверы наперегонки. Но докладывает она СВОИМ
            // чередом, а не держит запуск.
            var cached = LoadCachedManifest();
            LvnManifest manifest;
            bool online;
            if (cached != null)
            {
                LvnAsync.Fire(PinConnectivityAsync(probeTask, Mark), "BootProbe");
                LvnAsync.Fire(CatchUpManifestAsync(manifestTask, versionsTask), "ManifestCatchUp");
                manifest = cached;
                online = _assets.Loader.Reachable; // проба уточнит через миг
                // Имя игры — авторское, из каталога (заголовок хаба).
                BootVeil.Splash(cached.ui?.browse?.title ?? Application.productName);
                Mark("manifest (вчерашний кэш — сеть догоняет)");
                BootVeil.Progress(60);
            }
            else
            {
                // ПЕРВАЯ УСТАНОВКА: рисовать нечем, ждём сеть — как раньше.
                online = await probeTask;
                if (!online) LvnNetworkStatus.MarkOffline("boot healthz: server unreachable");
                Mark($"connectivity → {(online ? "online" : "offline")}");
                BootVeil.Progress(30, LvnWords.Of("boot.loading_data", "loading data…"));

                try { await versionsTask; } catch { /* offline: last-known index */ }
                Mark("version index");

                var boot = await ResolveManifestAsync(manifestTask, online, Mark);
                manifest = boot.manifest;
                online = boot.online;
            }
            // The awaits above outlive a destroyed host (scene switch, embedder
            // teardown) — never keep booting on a dead component. Пустой манифест
            // означает ровно это: ожидание сети прервали сносом компонента.
            if (_quitting.IsCancellationRequested || manifest == null) return;
            LvnLog.Info($"[lvn-app] manifest: {manifest.titles?.Count ?? 0} title(s) (online={online})");

            PrepareStage(manifest, Mark);
            Mark("stage + theme ready");
            _downloads = new DownloadManager(_assets.Loader);
            var prefetch = SafeBootPrefetch(manifest, online);
            _ = prefetch.ContinueWith(_ => LvnLog.Trace(
                $"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms boot prefetch settled (background)"),
                TaskScheduler.FromCurrentSynchronizationContext());

            await RestoreProgressAsync(manifest);

            _shell = NovelShell.Create(transform, theme: ShellTheme);
            _shell.Build(manifest, _assets);
            Mark("shell built");
            WireQuickMenu(manifest);

            WireShellScreens(manifest);

            // The FULL library warms in the background from here on: every
            // chapter of every title lands on disk while the player browses or
            // reads — the next chapter's loading screen is then near-instant,
            // and nothing EVER trickles in on camera. Yields to an active
            // chapter gate so it never steals that bandwidth.
            LvnAsync.Fire(WarmLibraryAsync(manifest, _quitting), "WarmLibrary");
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
            _leash.Hold(() => Lvn.Services.LvnAttribution.LinkOpened += ApplyDeepLink,
                        () => Lvn.Services.LvnAttribution.LinkOpened -= ApplyDeepLink);

            var run = _shell.RunAsync(
                bootReady: () => prefetch.IsCompleted,
                chapterReady: BeginChapterLoading,
                chapterProgress: ch => ChapterLoadProgress,
                playChapter: PlayChapterAsync,
                askName: AskName,
                ct: _quitting,
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
                var ct = _quitting;
                while (!prefetch.IsCompleted && !ct.IsCancellationRequested)
                {
                    float p = l != null && l.BatchTotal > 0
                        ? Mathf.Clamp01((float)l.BatchDone / l.BatchTotal) : 0f;
                    BootVeil.Progress(60 + Mathf.RoundToInt(p * 40f),
                        LvnNetworkStatus.IsOffline ? LvnOfflineText.Reconnecting : LvnWords.Of("boot.loading", "loading…"));
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
                    LvnLog.Trace($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms первый вход: брендовая вуаль до одетой сцены");
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
                    // ЗАСТАВКА ДОСТАИВАЕТ СВОЁ. Успели за полсекунды — тем
                    // лучше, но мелькнувшее и тут же исчезнувшее имя читается
                    // как сбой, а не как вступление.
                    // ТОКЕН — ТОТ ЖЕ `ct`, СНЯТЫЙ В НАЧАЛЕ МЕТОДА. Свойство
                    // destroyCancellationToken живёт НА КОМПОНЕНТЕ: читать его
                    // ПОСЛЕ await нельзя — к этому мигу объект мог быть
                    // уничтожен, и обращение бросит MissingReferenceException
                    // прямо в продолжении, где его некому поймать. Сама
                    // структура токена уничтожение переживает и честно
                    // отвечает «отменено» — потому копию и снимают один раз,
                    // до первого ожидания.
                    while (BootVeil.BrandHolding && !ct.IsCancellationRequested)
                        await System.Threading.Tasks.Task.Yield();
                    if (ct.IsCancellationRequested) return;
                    if (Stage != null && !Stage.HasBackdrop)
                        Debug.LogWarning($"[lvn-boot] полотно не встало за {wait.ElapsedMilliseconds}ms — снимаем вуаль без него");
                    await BootVeil.FadeOutAsync(LvnMenuStage.VeilFadeSeconds);
                }
                LvnLog.Trace($"[lvn-boot] +{bootClock.ElapsedMilliseconds}ms veil handed off — app boot done");
                // Первый ЭКРАН, а не первый кадр: между запуском и этим местом
                // человек смотрит на загрузку и может уйти. Без этой ступени
                // воронка первой сессии начинается сразу с «начал главу», и
                // потери на загрузке выглядят так, будто игра никому не нужна.
                // Длительность здесь же: «долго грузилось» — самая частая
                // причина уйти, не начав.
                Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.FirstScreen,
                    ("boot_ms", bootClock.ElapsedMilliseconds),
                    ("offline", LvnNetworkStatus.IsOffline));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                BootVeil.Hide();
            }
        }

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
        private void OnContentChanged() => LvnAsync.Fire(OnContentChangedAsync(), "OnContentChanged");

        /// <summary>
        /// ПОДКЛЮЧИТЬ ЭКРАНЫ ОБОЛОЧКИ К ПРИЛОЖЕНИЮ.
        ///
        /// <para>Оболочка умеет рисовать, но не знает, что делать: куда ведёт
        /// тап по валюте, откуда брать список треков меню, что показывать в
        /// кружке загрузок, сколько ещё не скачано. Всё это — работа хоста, и
        /// она здесь.</para>
        ///
        /// <para>Фаза вынута из запуска: сто тридцать строк подключения стояли
        /// посреди него, между постройкой оболочки и прогревом библиотеки, и
        /// читались как продолжение запуска, хотя это отдельная работа — и
        /// единственная, которую хост-встройщик захочет переписать под себя.</para>
        /// </summary>
        private void WireShellScreens(LvnManifest manifest)
        {
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
                Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.Device, Lvn.LvnDeviceProfile.Snapshot());

                _shell.Settings.StorageInfo = StorageInfoAsync;
                _shell.Settings.DownloadAll = DownloadEverythingAsync;
                _shell.Settings.ClearDownloads = async () =>
                {
                    long freed = await loader.ClearAssetCacheAsync();
                    LvnLog.Trace($"[lvn-content] загруженное удалено: {freed >> 20} МБ");
                };
                _shell.Settings.DownloadProgress = () =>
                    (loader.BatchBytesReceived, loader.BatchBytesExpected, loader.BatchActive);

                // СБРОС АККАУНТА. Обряд забвения (LvnForget.All) написан давно
                // и умеет всё — а в игре его не звал НИКТО: жил только в
                // тестах. Кого забывать, знает каталог: хранилища по новеллам
                // адресуются по ключу и списка своих ключей не ведут, а
                // гардероб живёт по ПЕРСОНАЖУ, а не по новелле.
                _shell.Settings.OnResetAccount = () =>
                {
                    var titles = new System.Collections.Generic.List<string>();
                    if (_manifest?.titles != null)
                        foreach (var t in _manifest.titles)
                            if (!string.IsNullOrEmpty(t?.id)) titles.Add(t.id);
                    var entities = new System.Collections.Generic.List<string>();
                    if (_manifest?.sprites != null)
                        foreach (var key in _manifest.sprites.Keys) entities.Add(key);

                    Lvn.UI.LvnForget.All(titles, entities);
                    // Забыли — значит и на экране должно стать пусто: витрина
                    // держит прогресс в своих карточках, шапка — имя игрока.
                    _shell.ApplyLiveUpdate(_manifest);
                    LvnLog.Info("[lvn-forget] сброс из настроек: игрок стёрт, воронка пойдёт заново");
                };

                // Единый навбар: валюты данными, бургер по контексту
                // (в сцене — квик-меню, в меню — настройки), пилюля — магазин.
                if (_shell.TopBar != null)
                {
                    _shell.TopBar.Currencies = HubCurrencies();
                    _shell.TopBar.RefreshBalances();
                    _shell.TopBar.OnCurrency = _ => LvnAsync.Fire(_shell.OpenPackShopAsync(), "TopBarStore");
                    _shell.TopBar.OnBurger = () =>
                    {
                        if (InChapter && Stage != null) Stage.OpenQuickMenu();
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
                        Lvn.UI.Screens.LvnIntro.Is(_currentTitle));
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
                    hud.Offline = () => Lvn.LvnNetworkStatus.IsOffline;
                    hud.PendingOps = () => Lvn.Services.LvnWallet.PendingCount;
                    hud.ActiveUrl = () => loader.LastStartedUrl;
                    hud.FlushPending = Lvn.Services.LvnWallet.FlushAsync;
                    hud.DownloadAll = DownloadEverythingAsync;
                    hud.ChaptersInfo = ChapterAvailability;
                    hud.CurrentChapterOffer = () =>
                    {
                        // Только во время сессии: вне игры «текущая глава» —
                        // хвост прошлого запуска («Скачать главу 0», скрин).
                        if (!InChapter) return null;
                        var t = _currentTitle; var ch = _currentChapter;
                        if (t == null || ch == null) return null;
                        long bytes = 0; int miss = 0;
                        void Probe(string url, string kind, long size)
                        {
                            if (string.IsNullOrEmpty(url)) return;
                            var eff = DownloadPolicy.Effective(kind, url);
                            if (loader.IsAssetCached(eff)) return;
                            miss++; bytes += size > 0 ? size : DownloadPolicy.UnknownSizeBytes;
                        }
                        foreach (var part in Lvn.Content.LvnParts.OfChapter(ch))
                            Probe(part.Url, part.Kind, part.Size);
                        if (miss == 0) return null;
                        string label = LvnWords.Of("download.chapter", "Download chapter {n} · ≈{mb} MB")
                    .Replace("{n}", ch.number.ToString())
                    .Replace("{mb}", Lvn.Content.LvnBytes.Short(bytes));
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
                                bytes += size > 0 ? size : DownloadPolicy.UnknownSizeBytes;
                                files++;
                                if (sample.Count < 8) sample.Add(url);
                            }
                        // Диагностика хвоста: если «скачал всё, а остаток не 0» —
                        // консоль называет виновников поимённо.
                        if (files != _lastMissingCount)
                        {
                            _lastMissingCount = files;
                            if (files > 0 && files <= 24)
                                LvnLog.Trace($"[lvn-content] недокачано {files}: {string.Join(", ", sample)}");
                        }
                        return (bytes, files);
                    };
                }
            }
        }

        /// <summary>
        /// ПРОГРЕСС ИГРОКА ВОЗВРАЩАЕТСЯ ДО ПЕРВОГО КАДРА ВИТРИНЫ.
        ///
        /// <para>Чистая установка (переустановка под той же учёткой, побитые
        /// настройки) поднимает прогресс: сперва из файлового дома — мгновенно
        /// и без сети, — потом из серверной копии. Делается это ДО того, как
        /// витрина нарисуется, иначе «Продолжить» в первом кадре покажет не ту
        /// главу и игрок нажмёт её раньше, чем правда приедет.</para>
        ///
        /// <para>Не чистое устройство — не повод пройти мимо. Подъём работал
        /// только на пустом, и второй телефон игрока, у которого свой прогресс
        /// есть, не узнавал о вечере, сыгранном на планшете, НИКОГДА. Здесь обе
        /// стороны настоящие: не восстановление, а слияние по правилам вида
        /// данных — потолок глав и галерея доливаются, закладка едет за тем
        /// устройством, где играли позже.</para>
        ///
        /// <para>Фаза вынута из общего запуска: тот дорос до трёхсот семидесяти
        /// строк, и в нём эта работа читалась как ещё один try посреди прочих.</para>
        /// </summary>
        private async Task RestoreProgressAsync(LvnManifest manifest)
        {
            try
            {
                if (ProgressVault.IsVirgin(manifest))
                {
                    ProgressVault.Apply(ProgressVault.ReadLocal(), manifest);
                    if (ProgressVault.IsVirgin(manifest) && _state != null)
                        ProgressVault.Apply(
                            await _state.LoadVarsAsync(ProgressVault.Scope, _quitting),
                            manifest);
                }
                else if (_state != null)
                {
                    // НЕ ЧИСТОЕ УСТРОЙСТВО — и это не повод пройти мимо. Подъём
                    // работал только на пустом, поэтому второй телефон игрока,
                    // у которого свой прогресс есть, не узнавал о вечере,
                    // сыгранном на планшете, НИКОГДА. Здесь обе стороны
                    // настоящие: не восстановление, а слияние по правилам вида
                    // данных (потолок и галерея доливаются, закладка едет за
                    // тем устройством, где играли позже).
                    ProgressVault.Absorb(
                        await _state.LoadVarsAsync(ProgressVault.Scope, _quitting),
                        manifest);
                }
            }
            catch (OperationCanceledException) { }   // приложение закрывают — не отказ
            catch (Exception e) { Debug.LogWarning("[lvn-vault] restore skipped: " + e.Message); }
        }

        /// <summary>
        /// Проба докладывает СВОИМ ЧЕРЕДОМ. Её вопрос — «сеть есть?», и ответ
        /// нужен последующим запросам (офлайн-признак заставляет их быстро
        /// падать в дисковый кэш вместо ожидания сокета). Но держать ради него
        /// ЗАПУСК незачем: витрина уже нарисована по вчерашнему каталогу.
        /// </summary>
        private static async Task PinConnectivityAsync(Task<bool> probe, Action<string> mark)
        {
            bool online;
            try { online = await probe; }
            catch { online = false; }
            if (!online) LvnNetworkStatus.MarkOffline("boot healthz: server unreachable");
            mark?.Invoke($"connectivity → {(online ? "online" : "offline")}");
        }

        /// <summary>
        /// СЕТЬ ДОГНАЛА. Запуск нарисовал витрину по вчерашнему каталогу; когда
        /// приезжает свежий, экраны обновляются на лету — тем же путём, что и по
        /// сигналу «контент сменился».
        ///
        /// <para>Каталог меняется редко, а принятие манифеста стоит недёшево
        /// (байты меню, пересборка витрины, тема, забытые облики). Поэтому
        /// сначала сверяем: тот же — расходимся молча.</para>
        /// </summary>
        private async Task CatchUpManifestAsync(Task<LvnManifest> manifestTask, Task versionsTask)
        {
            try { await versionsTask; } catch { /* offline: last-known index */ }
            LvnManifest fresh;
            try { fresh = await manifestTask; }
            catch (Exception ex)
            {
                LvnLog.Info($"[lvn-app] запуск по кэшу: сеть не догнала ({ex.Message})");
                return;
            }
            if (fresh == null || this == null) return;
            if (SameAsCached(fresh))
            {
                LvnLog.Info("[lvn-app] запуск по кэшу: каталог не менялся");
                return;
            }
            LvnLog.Info("[lvn-app] запуск по кэшу: приехал свежий каталог — обновляю экраны");
            await AdoptManifestAsync(fresh);
        }

        private async Task OnContentChangedAsync()
        {
            LvnLog.Info("[lvn-app] content changed — reloading");
            try { await _assets.WarmVersionsAsync(); } catch { /* offline */ }

            LvnManifest manifest;
            try { manifest = await FetchManifestAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[lvn-app] live manifest fetch failed: {ex.Message}"); return; }
            // НАС МОГЛИ ОБОГНАТЬ, ПОКА МЫ КАЧАЛИ. Тогда открытую главу
            // перезагружать нечем: скрипт поедет по каталогу, который уже
            // отменён, — и главу перебьёт вчерашний текст.
            if (!await AdoptManifestAsync(manifest)) return;
            await HotReloadOpenChapterAsync();
        }

        /// <summary>
        /// ПРИНЯТЬ СВЕЖИЙ МАНИФЕСТ — одна работа на два повода: сервер сказал
        /// «контент сменился» и запуск догнал сеть (витрина рисовалась по
        /// вчерашнему кэшу). Поводы разные, а список того, что обязано узнать о
        /// новом каталоге, — один: кэш, байты меню, экраны, дома слов, сцена,
        /// тема, надетые облики.
        /// </summary>
        private async Task<bool> AdoptManifestAsync(LvnManifest manifest)
        {
            if (manifest == null) return false;
            // ВАЖЕН ТОЛЬКО САМЫЙ НОВЫЙ КАТАЛОГ.
            //
            // Поводов принять его два — «сервер сказал, что контент сменился» и
            // «запуск догнал сеть», — и оба живут своим асинхронным трактом.
            // Между CacheManifest и ApplyManifest здесь лежит ожидание, и две
            // приёмки, начатые подряд, приходят В ЛЮБОМ ПОРЯДКЕ: сеть
            // очерёдности не обещает. Победивший последним ставил СВОЁ — и это
            // старое: приложение оставалось на отменённом каталоге, а
            // офлайновая копия записывалась вчерашней.
            //
            // Правило это уже записано у соседа (смена языка сверяет свой выбор
            // после КАЖДОГО ожидания и объясняет почему) — здесь его не было.
            int mine = _adoptions.Claim(ManifestLine);
            CacheManifest(manifest); // keep the offline copy fresh on every live update
            // Pull the changed boot-set bytes and re-warm replaced covers BEFORE the
            // carousel rebuilds — otherwise it re-renders from the stale in-memory
            // sprites and a cover swap on the server never shows up.
            try { await _downloads.MenuRefreshAsync(manifest, default); }
            catch { /* best-effort; never blocks the live update */ }
            if (!_adoptions.Mine(ManifestLine, mine))
            {
                LvnLog.Info("[lvn-app] приёмку каталога обогнали — этот экраны не трогает");
                return false;
            }
            _shell?.ApplyLiveUpdate(manifest);
            // Содержимое манифеста применяет тот же дом, что и на старте
            // (NovelApp.Manifest): два списка одного факта расходились при
            // следующем добавленном поле. Смена темы безопасна посреди реплики —
            // VnStage.ApplyTheme восстанавливает видимую строку и выборы.
            ApplyManifest(manifest);

            // ОБНОВЛЕНИЕ МЕНЯЕТ ФАЙЛЫ ПОД ТЕМИ ЖЕ ИМЕНАМИ. Сцена помнит надетый
            // облик по СПИСКУ СЛОЁВ — и после обновления сочла бы его прежним,
            // оставив на экране старый арт. Забываем надетое: реплей ниже
            // пересоберёт фигуры уже из новых файлов.
            Stage?.ForgetLooks();
            return true;
        }

        /// <summary>Линия приёмки каталога: важен только самый новый. Домом
        /// правила заведует <see cref="Lvn.UI.LvnNewest"/> — тот же, что держит
        /// дорожки сцены.</summary>
        private readonly Lvn.UI.LvnNewest _adoptions = new Lvn.UI.LvnNewest();
        private const string ManifestLine = "manifest";

        /// <summary>Открытая глава подхватывает изменившийся скрипт. Отдельно от
        /// принятия манифеста: на запуске главы нет, и этой работе там нечего
        /// делать.</summary>
        private async Task HotReloadOpenChapterAsync()
        {
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
                LvnLog.Trace($"[lvn-app] hot-swapped chapter '{_currentChapter.id}' in place (kept position)");
            }
            else
            {
                Stage.Play(json);
                if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                    Lvn.UI.LvnPlayerName.Seed(Stage.Player, _playerName);
                LvnLog.Trace($"[lvn-app] reloaded chapter '{_currentChapter.id}' (structure changed — restarted)");
            }
        }
    }
}
