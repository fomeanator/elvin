using System;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// БЫСТРОЕ МЕНЮ И ХОСТОВЫЕ ОПЫ — чем сценарий просит у приложения то, чего
    /// в движке нет: магазин, гардероб, настройки, галерею, вход, пуши.
    ///
    /// <para>Отдельным домом по границе ответственности, а не по размеру. Здесь
    /// одно правило на все пункты: КАЖДЫЙ ПОЯВЛЯЕТСЯ, ТОЛЬКО ЕСЛИ МАНИФЕСТ ЕГО
    /// ПРОСИТ. Игра без валюты не должна показывать магазин, новелла без
    /// гардероба — пункт гардероба; оболочка ничего не знает про конкретный
    /// продукт и лишь читает объявленное.</para>
    ///
    /// <para>И одно правило на все опы: пока экран открыт, история ДЕРЖИТСЯ
    /// (<c>ctx.Hold</c>) и продолжается ровно там, где стояла. Оп, забывший
    /// отпустить историю, вешает игру — поэтому отпускание живёт в тех же
    /// нескольких строках, что и открытие.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
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
                StageMenu.AddMenuItem(LvnWords.Pick("menu.store", storeCfg.menu_label, "Store"), stage => LvnAsync.Fire(_shell.OpenPackShopAsync(), "OpenPackShop"));
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
                StageMenu.AddMenuItem(LvnWords.Pick("menu.wardrobe", wardrobeCfg?.menu_label, "Wardrobe"),
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
            // Через Кассира, а не прямо в кошелёк: платный выбор — такая же
            // покупка, как вход в главу, и обряд у неё тот же. Мимо кассы
            // терялось ровно то, ради чего платные выборы и заводят: не хватило
            // — магазин не предлагался (игрок упирался в стену), а отказ не
            // попадал в отчёт, хотя «упёрся в цену» шлёт только Кассир.
            Stage.ChoiceSpend = (currency, amount) =>
                ChargeWithStoreAsync(currency, amount, "choice", "You need more to pick this.");

            // Test-build currency faucet (economy.debug_grant): a quick-menu item
            // that credits the wallet on tap — the partner's "получить 100" button
            // for exercising paid choices and the wardrobe without a store.
            var faucet = manifest.economy?.debug_grant;
            if (faucet != null && !string.IsNullOrEmpty(faucet.currency))
            {
                int amount = faucet.amount ?? 100;
                // Подстановка была обещана документацией поля и не делалась:
                // автор, скопировавший умолчание себе, видел «{amount}» буквально.
                string label = (faucet.label ?? "Получить {amount}").Replace("{amount}", amount.ToString());
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
            // Делегат сохраняется в переменную, а не пишется прямо в событие:
            // анонимную подписку отписать НЕЧЕМ — её и не отписывали.
            System.Action onPrefsChanged = () =>
            {
                // СЦЕНА ПЕРЕСОБИРАЕТСЯ, ТОЛЬКО ЕСЛИ СМЕНИЛСЯ ФАВОРИТ. Событие
                // настроек летит на КАЖДОЕ присваивание — в том числе на каждый
                // кадр перетаскивания ползунка громкости, — а пересборка сцены
                // меню это полотно, кукла и вся её диагностика. Сравнение
                // дешевле любой отложенной перерисовки: лишней работы не
                // становится меньше, её просто не возникает.
                var favNow = MenuFavoriteEntity();
                if (!_chapterPlaying && favNow != _lastMenuFavorite)
                {
                    _lastMenuFavorite = favNow;
                    ShowMenuScene(withPortal: false); // смена фаворита — живьём, без врат
                }
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
            _leash.Hold(() => Lvn.UI.LvnPrefs.Changed += onPrefsChanged,
                        () => Lvn.UI.LvnPrefs.Changed -= onPrefsChanged);

            // Зум к зоне скина (Илья 28.08: «как к лицу фаворитов в прологе»):
            // лист гардероба сообщает активный раздел — камера наезжает на
            // голову/шею/корпус куклы; «Все», «Во весь рост» и закрытие
            // возвращают общий план.
            Lvn.UI.Screens.WardrobeSheet.SectionFocus -= OnWardrobeSection;
            Lvn.UI.Screens.WardrobeSheet.SectionFocus += OnWardrobeSection;

            // СЦЕНА МЕНЮ НЕ ЗАВИСИТ ОТ МУЗЫКИ. Весь этот блок — витрина,
            // панорама полотна, переход в главу — стоял ВНУТРИ «если у меню есть
            // трек»: новелла без музыки оставалась без сцены меню и без
            // перехода вовсе, а связи между этими вещами нет никакой.
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
                // …БЕЗ ВРАТ: смена наряда — пересборка куклы, а не приход в
                // меню. Со створом это выглядело так, будто каждая юбка
                // открывает портал (живой репорт 28.08).
                Lvn.UI.LvnWardrobe.Changed += _ => { if (!_chapterPlaying) ShowMenuScene(withPortal: false); };
                // Сцена перехода: панель ведёт экран, створ и героиню — хост.
                _shell.OnPortalEnter = EnterPortalAsync;
                // РЕЖИМ ОБЪЯВЛЯЕТ ОБОЛОЧКА, а не хост: она владеет сессией
                // главы и уже говорит об этом Режиссёру. Здесь стоял второй
                // такой же вызов — оба идемпотентны, поэтому дубль не ломался,
                // но «кто объявляет режим» имело два ответа, а это ровно то,
                // от чего роль Режиссёра и заводилась.
                _shell.OnChapterSessionStart += () => { _menuBgSet = false; _menuMusic?.Pause(); HideMenuSceneActor(); };
                _shell.OnChapterSessionEnd += () =>
                {
                    if (_menuMusic != null && _menuMusic.clip != null) _menuMusic.UnPause();
                };
            }
            var menuTrack = ResolveMenuTrackUrl(manifest);
            if (!string.IsNullOrEmpty(menuTrack)) LvnAsync.Fire(StartMenuMusicAsync(menuTrack), "MenuMusic");

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
                {
                    _shell.Hub.OnDaily = () => _shell.OpenDailyAsync();
                    // НАГРАДУ НАДО ЕЩЁ И НАЧИСЛИТЬ. Экран поднимал OnClaim и
                    // помечал день забранным, но подключить это к кошельку
                    // должен хост — а он не подключал: игрок жал «Забрать»,
                    // ячейка гасла, денег не приходило. Начисление живёт у
                    // сервиса (он же обновляет кошелёк), здесь только провод.
                    if (_shell.Daily != null)
                        _shell.Daily.OnClaim = day => LvnAsync.Fire(ClaimDailyAsync(day), "ClaimDaily");
                }
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

        /// <summary>
        /// Показать таблицу лидеров, наполнив её живыми данными.
        ///
        /// <para>Экран показывал ДЕМО-строки: он их и заводит в конструкторе,
        /// чтобы в редакторе было видно вёрстку. Пока хост не подставит
        /// настоящие, игрок увидел бы выдуманные имена — поэтому наполнение и
        /// показ идут одним вызовом, а не двумя, которые можно перепутать
        /// местами.</para>
        ///
        /// <para>Сеть может не ответить: тогда показываем то, что есть, а не
        /// пустой экран. «Рейтинг недоступен» игроку сказать нечем — сервис
        /// не различает «пусто» и «не дозвонились».</para>
        /// </summary>
        private async Task ShowLeaderboardAsync(string board = "score")
        {
            if (_shell?.Leaderboard == null) return;
            try
            {
                var top = await Lvn.Services.LvnLeaderboard.GetTopAsync(board, 20);
                if (top?.Entries != null && top.Entries.Count > 0)
                {
                    var list = new System.Collections.Generic.List<LeaderboardScreen.Entry>();
                    string me = Lvn.UI.LvnPlayerName.Current;
                    for (int i = 0; i < top.Entries.Count; i++)
                        list.Add(new LeaderboardScreen.Entry
                        {
                            Rank = i + 1,
                            Name = top.Entries[i].Name,
                            Score = top.Entries[i].Score,
                            IsYou = !string.IsNullOrEmpty(me) && top.Entries[i].Name == me,
                        });
                    _shell.Leaderboard.Entries = list;
                    _shell.Leaderboard.Rebuild();
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] рейтинг не приехал: {ex.Message}"); }
            await _shell.OpenLeaderboardAsync();
        }

        /// <summary>
        /// Забрать ежедневную награду: начисляет сервер, он же обновляет
        /// кошелёк. Отказ показываем — молча погасшая ячейка без денег
        /// неотличима от удачи, а спросить не у кого.
        /// </summary>
        private async Task ClaimDailyAsync(int day)
        {
            bool ok = false;
            try { ok = await Lvn.Services.LvnDaily.ClaimAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] ежедневная награда: {ex.Message}"); }
            if (ok) return;
            Debug.LogWarning($"[novelapp] день {day}: награда не начислена");
            if (_shell != null)
                await _shell.AlertAsync(
                    Lvn.Content.LvnWords.Of("daily.failed_title", "Reward not claimed"),
                    Lvn.Content.LvnWords.Of("daily.failed", "The server did not confirm it — try again later."));
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
                        Lvn.UI.LvnPlayerName.Set(nick);   // одно хранилище на всех
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
    }
}
