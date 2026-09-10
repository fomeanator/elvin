using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ИГРАЕМ ГЛАВУ — часть <see cref="NovelApp"/>: вход в главу и всё, что он
    /// за собой тянет — доставка скрипта, прогрев библиотеки, тема новеллы,
    /// сейв, возврат в меню.
    ///
    /// <para>Самая длинная процедура приложения: она трогает сцену, кошелёк,
    /// хранилище, оболочку и аналитику разом — и именно поэтому её незачем
    /// держать посреди всего остального.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // Play a title from its entry point and KEEP GOING: when a chapter finishes,
        // the next one (by number) follows seamlessly — the player reads the whole
        // novel without bouncing off the carousel between episodes. A progress
        // marker remembers the furthest chapter started, so re-entering the title
        // continues there (and the in-chapter autosave restores the exact line);
        // finishing the last chapter clears it so a replay starts clean.
        private async Task PlayChapterAsync(LvnTitle title, LvnChapter chapter, string playerName)
        {
            // ЧЕМ ЭТОТ ЗАХОД ЯВЛЯЕТСЯ — спрашиваем у дома прогресса: с какой
            // главы, впервые ли, и оплачен ли уже вход. Четыре сплетённых
            // правила стояли здесь же, и порядок между ними держался
            // комментариями (см. LvnProgress.BeginEntry).
            var entry = LvnProgress.BeginEntry(title, chapter);
            chapter = entry.Chapter;
            bool novelFreshStart = entry.NovelFreshStart;
            bool alreadyEntered = entry.AlreadyPaid;
            while (chapter != null)
            {
                // The script must be REACHABLE before anything is charged — an
                // offline entry used to burn the energy and silently bounce to
                // the menu (and charge AGAIN on the retry).
                if (!await EnsureChapterScriptAsync(chapter))
                {
                    var eco = _manifest?.economy;
                    await _shell.AlertAsync(
                        _chapterMissingOnServer ? LvnOfflineText.ChapterMissingTitle
                                                : (eco?.gate_title ?? LvnOfflineText.Title),
                        _chapterMissingOnServer ? LvnOfflineText.ChapterMissing
                                                : LvnOfflineText.ChapterNeedsNetwork);
                    break;
                }
                if (!alreadyEntered && !await ChargeChapterEntryAsync(chapter))
                    break; // couldn't/wouldn't pay the entry cost → back to the carousel
                alreadyEntered = false;
                // Stream this chapter's asset plan. The FIRST chapter's plan was
                // started under the loading screen (BeginChapterLoading); a resume
                // into a later chapter, or a seamless next chapter, starts its own
                // here — critical assets first, deferred during play.
                if (_downloads != null && !ReferenceEquals(chapter, _preparedChapter))
                    _chapterSched = _downloads.BeginChapter(chapter, _quitting);
                _preparedChapter = null;
                AnnounceChapterStart(title, chapter);
                var finished = await PlayOneChapterAsync(title, chapter, playerName, novelFreshStart);
                novelFreshStart = false; // only the entry chapter of this run counts
                if (finished == null)
                {
                    AnnounceChapterAbandon(title, chapter);
                    break; // → carousel
                }
                AnnounceChapterFinish(title, finished);
                // A cross-chapter save load can land the player in another title —
                // continue along whichever title the finished chapter belongs to.
                var (owner, _) = FindChapterByScriptUrl(finished.script_url);
                if (owner != null) title = owner;
                var next = NextChapterOf(title, finished);
                // The FINISH is what advances progress — not the «Дальше» tap.
                // Leaving via the chapter-end menu used to strand the marker on
                // the finished chapter, and «Играть» replayed it from the top.
                LvnProgress.FinishChapter(title, next);
                if (next == null)
                {
                    // ВОРОНКА ПРОЙДЕНА — ПРЯМО ЗДЕСЬ, ФАКТОМ ФИНАЛА. Ворота в
                    // оболочке выводили это из reached/Current и на живом
                    // устройстве промахивались — партнёр получил «пролог по
                    // кругу» на чистой установке. Финал последней главы
                    // вводной — единственный надёжный свидетель.
                    Lvn.UI.Screens.LvnIntro.NoteFinished(title);
                }
                SyncProgressVault();
                // Between-chapters screen (ui.chapter_end): "Конец главы" with
                // continue/menu. Without it chapters flow seamlessly, as before.
                // ВВОДНАЯ НЕ СПРАШИВАЕТ «в меню?». Ей некуда больше вести:
                // пролог кончился, витрина открылась, и кнопка между ними —
                // лишний щелчок на месте перехода, который должен быть
                // непрерывным (героиня выходит из главы прямо в меню).
                bool intro = Lvn.UI.Screens.LvnIntro.Is(title);
                if (_shell?.ChapterEnd != null && !(intro && next == null))
                {
                    bool goNext = await _shell.ChapterEnd.ShowAsync(finished.name, hasNext: next != null);
                    if (!goNext || next == null) break;
                }
                else if (next == null) break;
                chapter = next;
            }
            // Новелла отыграна (или игрок ушёл) — кадр переходит меню тем же
            // непрерывным движением, что и по кнопке выхода: героиня
            // возвращается с миссии, а не появляется заново на пустой сцене.
            await ReturnToMenuAsync();
            // Back to the menu — stop the chapter scheduler so its deferred
            // downloads don't keep competing with the menu's own refresh.
            _downloads?.EndChapter();
            _chapterSched = null;
            // Вышли из новеллы: события меню не должны числиться за историей,
            // из которой игрок уже ушёл.
            LeaveChapterContext();
            // A chapter's worth of remote sprites fragments the panel's dynamic
            // atlas (freed regions rarely fit the next tenant); rebuild it clean
            // at this natural boundary.
            try
            {
                var panel = Stage != null
                    ? Stage.GetComponent<UIDocument>()?.rootVisualElement?.panel : null;
                if (panel != null) RuntimePanelUtils.ResetDynamicAtlas(panel);
            }
            catch { /* atlas reset is an optimization, never a failure */ }
        }

        /// <summary>
        /// ГЛАВА НАЧАЛАСЬ — обряд из пяти шагов, который обязан пройти ЦЕЛИКОМ.
        ///
        /// <para>Точка прогресса едет, объявляется КОНТЕКСТ (пока игрок внутри
        /// новеллы, каждое событие обязано знать, в какой именно — без этого
        /// сбой не отнести к истории, а таких событий в отчёте больше
        /// половины), обнуляется счёт воронки (она считается ПО ГЛАВЕ), свёрток
        /// прогресса догоняет все три хранилища, и только потом о начале
        /// узнают хост и аналитика.</para>
        ///
        /// <para>Шаги стояли в теле игрового цикла подряд, и порядок между ними
        /// держался соседством строк. Забыть один — значит получить события без
        /// адреса, воронку, склеенную из двух глав, или расхождение свёртка.</para>
        /// </summary>
        private void AnnounceChapterStart(LvnTitle title, LvnChapter chapter)
        {
            LvnProgress.StartChapter(title, chapter);   // ход прогресса сводит свёрток сам
            EnterChapterContext(title, chapter);
            lock (_reachedLabels) _reachedLabels.Clear();
            ChapterStarted?.Invoke(title, chapter);
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChapterStart,
                ("title", title?.id), ("chapter", chapter.id));
        }

        /// <summary>ГЛАВА КОНЧИЛАСЬ — обратная сторона того же обряда: хост,
        /// аналитика и слив незнакомых команд. Порядок тот же, что у начала:
        /// сперва хост, потом отчёт.</summary>
        private void AnnounceChapterFinish(LvnTitle title, LvnChapter finished)
        {
            ChapterFinished?.Invoke(title, finished);
            // ПЛАВНОСТЬ — ВЕЛИЧИНА, А НЕ ОЩУЩЕНИЕ. Счёт запинок уходит вместе с
            // концом главы: по нему видно, стало ли лучше после правки, — раньше
            // это можно было только почувствовать.
            var (hitches, worstMs) = Lvn.LvnFrameWatch.Take();
            // ЖИВЫХ ВХОДОВ В ПОЛОСУ — столько, сколько актёров и фонов было на
            // экране. Число заметно больше означает, что фоновая работа ходит
            // по сети как живая: ступень объявлена не тому, кто её спросит.
            LvnLog.Trace(Lvn.Content.LvnLaneWatch.Report());
            var (liveEnters, worstWaitMs, bgEnters, yields) = Lvn.Content.LvnLaneWatch.Take();
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChapterFinish,
                ("title", title?.id), ("chapter", finished.id),
                ("hitches", hitches), ("worst_ms", worstMs),
                ("lane_live", liveEnters), ("lane_wait_ms", worstWaitMs),
                ("lane_bg", bgEnters), ("lane_yields", yields));
            FlushUnknownOps(title, finished);
        }

        /// <summary>
        /// УХОД ИЗ СЕРЕДИНЫ ГЛАВЫ — третья сторона того же обряда, рядом с
        /// началом и концом.
        ///
        /// <para>Без этого события потеря внутри главы выводилась вычитанием
        /// (start минус finish), и в одно число сливались крах, гибель,
        /// упёршийся в энергию и просто заскучавший. Позиция говорит, ГДЕ
        /// бросили: у «дочитал до середины и вышел» и «вылетело на первом
        /// кадре» разные причины и разные починки.</para>
        ///
        /// <para>Контекст КАДРА, а не только позиции. «Ушли на команде 137» не
        /// отвечает ни на что: половина глав вообще без выборов, и бросают там
        /// не из-за развилки, а из-за того, ЧТО на экране — плохой спрайт, не
        /// тот фон, зависшая сцена. Метка, фон и кто на сцене дают место,
        /// которое можно открыть и посмотреть глазами.</para>
        ///
        /// <para>Стояло это всё прямо в цикле — и там же разошлось с концом
        /// главы: конец ЗАБИРАЕТ счёт запинок (Take, со сбросом), а уход читал
        /// те же счётчики, не сбрасывая. Запинки брошенной главы утекали в
        /// следующую и портили её число — ту самую величину, ради которой счёт
        /// и заведён.</para>
        /// </summary>
        private void AnnounceChapterAbandon(LvnTitle title, LvnChapter chapter)
        {
            var snap = Stage?.Player?.Save();
            // Брошенная глава — самый интересный случай для плавности: уходят
            // чаще всего оттуда, где дёргается.
            var (hitches, worstMs) = Lvn.LvnFrameWatch.Take();
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChapterAbandon,
                ("title", title?.id), ("chapter", chapter?.id),
                ("at", Stage?.Player?.Index ?? -1),
                ("label", snap?.AnchorStableLabel ?? snap?.AnchorLabel),
                ("bg", Lvn.UI.VnStage.LastSceneBgUrl),
                ("actors", Stage?.ActorsOnStage()),
                ("hitches", hitches), ("worst_ms", worstMs));
            FlushUnknownOps(title, chapter);
        }

        // Preflight: make the chapter's script locally available (cache hit or
        // a live fetch) BEFORE the entry charge — money never burns on a
        // chapter that can't start. The later fetch inside PlayOneChapterAsync
        // then hits the cache.
        // ПОЧЕМУ ГЛАВА НЕ ОТКРЫЛАСЬ — вопрос игрока, а не наш. «Нет сети» и
        // «главы нет на сервере» выглядят одинаково только изнутри: снаружи
        // первый идёт проверять вайфай, а второму проверять нечего, потому что
        // виноват автор. Причина последней неудачи живёт здесь, чтобы
        // сообщение называло её словом.
        private bool _chapterMissingOnServer;

        private async Task<bool> EnsureChapterScriptAsync(LvnChapter chapter)
        {
            _chapterMissingOnServer = false;
            if (chapter == null || string.IsNullOrEmpty(chapter.script_url)) return false;
            if (_assets.Loader.IsScriptCached(chapter.script_url)) return true;
            try
            {
                var json = await _assets.Loader.DownloadScriptCached(chapter.script_url);
                return !string.IsNullOrEmpty(json);
            }
            catch (Lvn.Content.LvnFetchException e)
            {
                // 404 — файла нет на сервере: сеть исправна, глава не выложена.
                // Спрашиваем ДОМ (см. LvnFetchException.MissingOnServer), а не
                // разбираем код строкой здесь.
                _chapterMissingOnServer = e.MissingOnServer;
                return false;
            }
            catch { return false; }
        }

        // Живая лестница и её отмена: смена качества и приход обновлений
        // начинают её заново, а прежний проход обязан остановиться — иначе две
        // очереди делят полосу и обе едут вдвое медленнее.
        private System.Threading.CancellationTokenSource _ladder;
        private LvnManifest _ladderManifest;
        private bool _ladderWired;
        private string _ladderQuality;

        /// <summary>Настройки менялись — если это была ступень качества,
        /// лестница начинается заново. Прочие настройки её не касаются:
        /// перезапуск на каждый ползунок громкости стоил бы полосы.</summary>
        private void OnQualityMaybeChanged()
        {
            var now = EffectiveArtQuality();
            if (now == _ladderQuality) return;
            LvnLog.Info($"[lvn-warm] ступень качества сменилась ({_ladderQuality} → {now}) — лестница заново");
            StartLadder(null);
        }

        /// <summary>
        /// ПУСТИТЬ ЛЕСТНИЦУ ЗАНОВО. Зовётся на старте, при смене ступени
        /// качества и когда сервер объявил новые версии файлов.
        ///
        /// <para>СКАЧАННОЕ НЕ УДАЛЯЕТСЯ. При смене качества прежние файлы
        /// остаются на диске: ими рисуют, пока новые не доехали, и они же
        /// пригодятся, если игрок вернёт прежнюю ступень. Отменяется только
        /// то, что в полёте, — оно уже не нужно. Место разбирает квота кэша,
        /// а не смена настройки («что будет при смене качества?» — Илья
        /// 10.09).</para>
        /// </summary>
        private void StartLadder(LvnManifest manifest)
        {
            if (manifest != null) _ladderManifest = manifest;
            if (_ladderManifest == null) return;
            if (!_ladderWired)
            {
                _ladderWired = true;
                // Сменили ступень качества — лестница идёт заново: прежние
                // файлы остаются на диске, а новых у неё ещё нет. Подписка
                // живёт столько же, сколько приложение, и снимается вместе с
                // ним: хозяин один, пересоздания у него нет.
                LvnPrefs.Changed += OnQualityMaybeChanged;
                Application.quitting += () => LvnPrefs.Changed -= OnQualityMaybeChanged;
            }
            _ladderQuality = EffectiveArtQuality();
            var prev = _ladder;
            _ladder = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_quitting);
            Lvn.LvnCancel.Retire(prev);   // гасит и отпускает: один Cancel оставил бы регистрации жить
            LvnAsync.Fire(WarmLibraryAsync(_ladderManifest, _ladder.Token), "WarmLibrary");
        }

        // ЛЕСТНИЦА ЗАГРУЗКИ — ОДИН ПОРЯДОК НА ВСЮ ИГРУ.
        //
        // Правило поверх всех ступеней: очередь определяется МОМЕНТОМ
        // ПОЯВЛЕНИЯ НА ЭКРАНЕ, а не сущностью. Отсюда и порядок: фаворит из
        // первой сцены не ждёт нарядов героини, потому что наряды нужны в
        // гардеробе, а его открывают позже первой сцены (согласовано с Ильёй
        // 10.09; описание ступеней — docs/loading-ladder.md).
        //
        // Лестница не останавливается никогда: она уступает живому кадру доли
        // секунды и едет дальше. Прежняя политика парковала её на всё время
        // главы, и на медленном телефоне библиотека не качалась почти никогда.
        private async Task WarmLibraryAsync(LvnManifest manifest, System.Threading.CancellationToken ct)
        {
            try
            {
                // Один кадр на то, чтобы бут отдал вуаль, — и поехали.
                await Task.Delay(300, ct);
                // Веса каталога — чтобы загрузчик говорил мегабайтами, а не
                // числом файлов. Не доехали — считаем файлами, лестница едет.
                await Lvn.Content.LvnCatalogSize.LoadAsync(_assets?.Loader, ct);
                int warmed = 0, skipped = 0;

                async Task<bool> WaitForQuietAsync()
                {
                    // Уступаем ЖИВОЙ ПОВЕРХНОСТИ — кадру, который игрок видит
                    // прямо сейчас. Это доли секунды, а не остановка.
                    while (_assets.LivePressure > 0 && !ct.IsCancellationRequested)
                        await Task.Delay(150, ct);
                    if (Lvn.LvnNetworkStatus.IsOffline) { await Task.Delay(3000, ct); return false; }
                    return !ct.IsCancellationRequested;
                }

                // ОБОЗОМ, А НЕ ПО ОДНОМУ: на мобильной сети цена файла — не
                // байты, а круговой рейс, и последовательный обход платит его
                // за каждый файл. Обоз к тому же ведёт счёт, иначе индикатор
                // видит один файл в полёте и врёт игроку об очереди.
                async Task Ступень(Lvn.Content.LvnRung rung,
                                   System.Collections.Generic.List<Lvn.Content.PreloadItem> pack)
                {
                    if (pack == null || pack.Count == 0) return;
                    if (!await WaitForQuietAsync()) return;
                    int missing = 0;
                    foreach (var it in pack)
                        if (_assets.Loader.IsAssetCached(it.Url)) skipped++; else missing++;
                    if (missing == 0) return;
                    using (Lvn.Content.LvnRungScope.At(rung))
                        try { await _assets.Loader.StartPreloadBatch(pack, ct); warmed += missing; }
                        catch (System.OperationCanceledException) { throw; }
                        catch { /* самолечение закроет отдельные файлы */ }
                }

                // Запись обоза идёт ЧЕРЕЗ ДОМ (PreloadItem.Of): он подставляет
                // ступень качества. Сырой адрес тянул бы исходники крупного
                // арта — почти десять мегабайт на слой облика вместо
                // полумегабайта.
                System.Collections.Generic.List<Lvn.Content.PreloadItem> Пачка(
                    System.Collections.Generic.IEnumerable<Lvn.Content.LvnPart> parts)
                {
                    var list = new System.Collections.Generic.List<Lvn.Content.PreloadItem>();
                    foreach (var part in parts)
                        if (!string.IsNullOrEmpty(part.Url)) list.Add(Lvn.Content.PreloadItem.Of(part));
                    return list;
                }

                System.Collections.Generic.IEnumerable<Lvn.Content.LvnPart> КадрГлавы(LvnChapter ch, bool первый)
                {
                    foreach (var part in Lvn.Content.LvnParts.OfChapter(ch))
                    {
                        bool критично = Lvn.Content.LvnPriority.OfChapterPart(part, current: true)
                                        == Lvn.Content.LvnRung.FirstFrame;
                        if (критично == первый) yield return part;
                    }
                }

                async Task СкриптГлавы(LvnChapter ch)
                {
                    if (ch == null || string.IsNullOrEmpty(ch.script_url)) return;
                    if (_assets.Loader.IsScriptCached(ch.script_url)) return;
                    try { await _assets.Loader.DownloadScriptCached(ch.script_url); }
                    catch { /* прогрев — оптимизация: попросят при входе */ }
                }

                // Вводная — та, с которой начинают все; остальные идут за ней.
                var вводная = (LvnTitle)null;
                var прочие = new System.Collections.Generic.List<LvnTitle>();
                if (manifest?.titles != null)
                    foreach (var t in manifest.titles)
                    {
                        if (t == null) continue;
                        if (вводная == null && LvnIntro.Is(t)) вводная = t;
                        else прочие.Add(t);
                    }
                var главы = ГлавыПоПорядку(вводная);

                // ── 2. ПЕРВЫЙ КАДР ГЛАВЫ НОЛЬ ────────────────────────────────
                // Скрипт, первый фон, первая поза агента и героиня в ОДНОМ
                // облике: «фон, агент, героиня с одной эмоцией, фавориты —
                // чтобы картинка всегда успевала к загрузке» (Илья 10.09).
                var перваяГлава = главы.Count > 0 ? главы[0] : null;
                await СкриптГлавы(перваяГлава);
                var первый = Пачка(КадрГлавы(перваяГлава, первый: true));
                первый.AddRange(Пачка(Lvn.Content.LvnParts.OfHeroBase(manifest)));
                await Ступень(Lvn.Content.LvnRung.FirstFrame, первый);
                if (ct.IsCancellationRequested) return;

                // ── 3. ОСТАТОК ГЛАВЫ НОЛЬ ────────────────────────────────────
                // Фавориты, прочие фоны и позы, музыка сцены.
                await Ступень(Lvn.Content.LvnRung.CurrentChapter, Пачка(КадрГлавы(перваяГлава, первый: false)));
                if (ct.IsCancellationRequested) return;

                // ── 4. ГЕРОИНЯ ЦЕЛИКОМ ───────────────────────────────────────
                // Все эмоции и весь гардероб: его открывают из меню в первые же
                // минуты, и пустые карточки читаются как «здесь ничего нет».
                await Ступень(Lvn.Content.LvnRung.Hero, Пачка(Lvn.Content.LvnParts.OfHero(manifest)));
                if (ct.IsCancellationRequested) return;

                // ── 5. ВИТРИНА ───────────────────────────────────────────────
                // Обложки всех новелл и первый кадр первой главы каждой: любая
                // новелла обязана открываться сразу, даже если целиком не
                // докачана.
                var витрина = new System.Collections.Generic.List<Lvn.Content.PreloadItem>();
                foreach (var t in прочие)
                {
                    витрина.AddRange(Пачка(Lvn.Content.LvnParts.OfTitleArt(t)));
                    var ch0 = FirstChapterOf(t);
                    if (ch0 != null) витрина.AddRange(Пачка(КадрГлавы(ch0, первый: true)));
                }
                await Ступень(Lvn.Content.LvnRung.Shelf, витрина);
                if (ct.IsCancellationRequested) return;

                // ── 6. ДРУГИЕ ГЕРОИ ──────────────────────────────────────────
                // Базовый облик каста: их встречают в ближайших сценах. Полный
                // разворот по осям остаётся на последнюю ступень.
                await Ступень(Lvn.Content.LvnRung.Spare, Пачка(Lvn.Content.LvnParts.OfCast(manifest)));
                if (ct.IsCancellationRequested) return;

                // ── 7-8. СЛЕДУЮЩАЯ ГЛАВА, ЗАТЕМ ОСТАЛЬНЫЕ ────────────────────
                // Порядок глав — авторский: игрок идёт по ним подряд, и очередь
                // идёт за ним. Внутри главы сперва её первый кадр.
                for (int i = 1; i < главы.Count; i++)
                {
                    var ch = главы[i];
                    await СкриптГлавы(ch);
                    var rung = i == 1 ? Lvn.Content.LvnRung.NextChapter : Lvn.Content.LvnRung.Library;
                    await Ступень(rung, Пачка(КадрГлавы(ch, первый: true)));
                    await Ступень(rung, Пачка(КадрГлавы(ch, первый: false)));
                    if (ct.IsCancellationRequested) return;
                }

                // ── 9. ОСТАЛЬНЫЕ НОВЕЛЛЫ ЦЕЛИКОМ ─────────────────────────────
                foreach (var t in прочие)
                    foreach (var ch in ГлавыПоПорядку(t))
                    {
                        await СкриптГлавы(ch);
                        await Ступень(Lvn.Content.LvnRung.Library, Пачка(КадрГлавы(ch, первый: true)));
                        await Ступень(Lvn.Content.LvnRung.Library, Пачка(КадрГлавы(ch, первый: false)));
                        if (ct.IsCancellationRequested) return;
                    }

                // ── 10. ЗАПАС ────────────────────────────────────────────────
                // Звучание оболочки и всё, чего лестница не назвала поимённо:
                // добавленное в манифест завтра доедет здесь, даже если про
                // него забыли, — но последним, ничего не задерживая.
                await Ступень(Lvn.Content.LvnRung.Spare, Пачка(Lvn.Content.LvnParts.OfAll(manifest)));

                LvnLog.Trace($"[lvn-warm] лестница пройдена ({warmed} скачано, {skipped} уже на диске)");
            }
            catch (System.OperationCanceledException) { /* teardown */ }
        }

        /// <summary>Главы новеллы подряд, как их читает игрок.</summary>
        private static System.Collections.Generic.List<LvnChapter> ГлавыПоПорядку(LvnTitle t)
        {
            var list = new System.Collections.Generic.List<LvnChapter>();
            if (t?.seasons == null) return list;
            foreach (var se in t.seasons)
            {
                if (se?.chapters == null) continue;
                foreach (var ch in se.chapters)
                    if (ch != null) list.Add(ch);
            }
            return list;
        }

        /// <summary>Первая глава новеллы — та, с которой начинают все.</summary>
        private static LvnChapter FirstChapterOf(LvnTitle t)
        {
            var all = ГлавыПоПорядку(t);
            return all.Count > 0 ? all[0] : null;
        }
    }
}
