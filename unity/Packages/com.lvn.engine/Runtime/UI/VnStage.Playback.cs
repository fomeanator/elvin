using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Chapter playback lifecycle: starting a script (Play / hot-swap), the
    /// staged opening warmups, the stage reset between scenes, the dialogue
    /// backlog, rollback, and the skip / auto-advance gears.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── skip (fast-forward) ──────────────────────────────────────────────
        // The genre's re-read gear: lines fly by until something needs the
        // player — a choice, a tap, the chapter's end, an opened menu.

        /// <summary>True while fast-forward is running.</summary>
        public bool Skipping { get; private set; }

        // A choice always gears skip down — the player must SEE and pick it,
        // never blow past a decision blind. But once she's consciously tapped
        // an option, there's no more danger: re-engage skip right after
        // CommitChoice instead of making a re-read stop and re-arm skip at
        // every single choice (miserable for QA replaying old chapters, and
        // no safer for a real player who just made the pick herself).
        private bool _resumeSkipAfterChoice;

        /// <summary>ГЛАВА ИДЁТ: чтец есть и он не доигран. Вопрос задавался
        /// пятью местами тремя частями каждое — и в одном из них выродился в
        /// «пропуск идёт И чтец есть И не доигран И на выборе», где первые три
        /// части уже были проверены строкой выше.</summary>
        private bool Playing => _player != null && !_player.Finished;

        /// <summary>Fast-forward lines until a choice, a tap, or the chapter ends.</summary>
        public void StartSkip()
        {
            if (!Playing) return;
            Skipping = true;
        }

        public void StopSkip() { Skipping = false; _resumeSkipAfterChoice = false; }

        private void SkipTick()
        {
            if (!Skipping) return;
            if (!Playing || _player.AtChoice)
            {
                if (Playing && _player.AtChoice)
                    _resumeSkipAfterChoice = true; // gearing down FOR a choice, not a stop/finish
                Skipping = false; // something needs the player — gear down
                return;
            }
            if (InputBlocked || _chromeHidden || StageBusy) return; // paused, not cancelled
            if (_dialogue != null && _dialogue.IsRevealing) { _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // ── auto-advance ─────────────────────────────────────────────────────
        // Reading delay after the reveal completes, scaled by line length and the
        // player's preference — the standard hands-free mode.
        private float _autoRevealDoneAt = -1f;
        private int _lastSayLength;

        /// <summary>Extra gate a host/menu can close to hold auto-advance (and
        /// tap handling) while an overlay is up. An open shared panel
        /// (<see cref="VnPanelHost"/>) blocks implicitly, so a wardrobe sheet or
        /// in-script screen can't be tapped/auto-advanced through.</summary>
        public bool InputBlocked
        {
            // ВЫВОДИТСЯ, а не хранится: ввод держит любая поверхность, которую
            // Режиссёр видит на экране (лист истории, квик-меню, модаль
            // оболочки), плюс короткий хвост после закрытия панели. Флаг
            // остаётся хосту, который держит историю по своей причине.
            get => _inputBlockedFlag
                || LvnScreenDirector.Current.SceneSurfaceOpen
                || (_panelHost != null && _panelHost.IsOpen)
                || !_clock.Passed(PanelGuardBarrier);
            set => _inputBlockedFlag = value;
        }
        private bool _inputBlockedFlag;

        /// <summary>
        /// СЦЕНА ЗАНЯТА САМА СОБОЙ — идёт `wait` или открыта форма ввода.
        ///
        /// <para>Ни листание, ни авточтение в это время работать не должны:
        /// они бы проскочили паузу, которую поставил автор, и съели строку,
        /// которую игрок ещё печатает.</para>
        ///
        /// <para>Пара флагов складывалась в четырёх местах, и в каждом чуть
        /// по-своему. Пока их складывают руками, «а этот случай тоже сюда?» —
        /// вопрос, который каждый раз решают заново.</para>
        /// </summary>
        private bool StageBusy => _awaitingWait || _awaitingInput;

        /// <summary>
        /// ТАП СЕЙЧАС НЕ НАШ — его забирает не продвижение истории.
        ///
        /// <para>Форма ввода забирает касание целиком. `wait` его глотает — НО
        /// НЕ НА ЭКРАНЕ С ГОРЯЧИМИ ТОЧКАМИ: там щелчок обязан дойти до точки и
        /// снять таймер, иначе поиск предмета замирает навсегда. Оговорку
        /// помнили в двух местах из двух — но записана она была дважды, и
        /// второй раз комментарием «то же, что выше».</para>
        /// </summary>
        private bool TapNotOurs => _awaitingInput || (_awaitingWait && _hotspots.Count == 0);

        /// <summary>«Не принимать ввод до этого момента» — обычный барьер, и
        /// держит его Хронометрист, как все прочие сроки сцены.</summary>
        private const string PanelGuardBarrier = "panel-guard";

        /// <summary>Closing an overlay and releasing its button happen in the
        /// same physical gesture. Keep that release away from the newly restored
        /// line, and restart auto-reading from that line rather than from time
        /// spent inside the wardrobe.</summary>
        private void ArmPanelInputGuard(float seconds)
        {
            _clock.Hold(PanelGuardBarrier, seconds); // барьер продлевается, а не переустанавливается
            _autoRevealDoneAt = -1f;
            _pressTracking = false;
            _suppressTap = true;
            _longPress?.Pause();
        }

        /// <summary>Set when the player asks to leave the chapter (the quick
        /// menu's Exit). The host's play loop watches it and returns to the
        /// title screen; position and stats are already autosaved, so Continue
        /// brings the player back to this exact line.</summary>
        public bool ExitRequested { get; private set; }

        /// <summary>Player-initiated exit to the menu: autosave the position,
        /// then signal the host loop.</summary>
        public void RequestExit()
        {
            AutosaveNow();
            ExitRequested = true;
        }

        /// <summary>Host acknowledgment — called by the play loop once it has
        /// acted on the request (and by Play for a fresh chapter).</summary>
        public void ClearExitRequest() => ExitRequested = false;

        /// <summary>
        /// СКОЛЬКО ДЕРЖИТСЯ ДОЧИТАННАЯ РЕПЛИКА в авторежиме: постоянная пауза
        /// плюс время на длину строки. Обе величины стояли безымянными в одной
        /// формуле, хотя решают они разное: первая — «сколько нужно, чтобы
        /// понять, что реплика кончилась», вторая — «сколько читается один
        /// символ».
        ///
        /// <para>Игрок правит это ползунком «задержка авто» — множитель
        /// <see cref="LvnPrefs.AutoDelayScale"/> сверху; здесь база, от которой
        /// ползунок отсчитывает.</para>
        /// </summary>
        private const float AutoPauseBase = 0.55f;
        private const float AutoPausePerChar = 0.035f;

        private void AutoAdvanceTick()
        {
            if (!LvnPrefs.AutoAdvance || InputBlocked || _chromeHidden
                || !Playing || _player.AtChoice
                || !_awaitingTap || StageBusy
                || EntryGatePending
                || _dialogue == null || _dialogue.IsRevealing)
            {
                _autoRevealDoneAt = -1f;
                return;
            }
            // First tick after the reveal finished: start the reading timer.
            if (_autoRevealDoneAt < 0f)
            {
                _autoRevealDoneAt = LvnClock.Now();
                return;
            }
            float delay = (AutoPauseBase + AutoPausePerChar * _lastSayLength)
                          * LvnPrefs.AutoDelayScale;
            // Через LvnClock: на реальном времени свёрнутая на минуту игра
            // возвращалась с «реплику читали минуту» и листала её сразу.
            if (LvnClock.Since(_autoRevealDoneAt) < delay) return;
            _autoRevealDoneAt = -1f;
            _awaitingTap = false;
            _player.Advance();
        }

        /// <summary>
        /// Live-edit hot-swap: replace the running chapter's script WITHOUT
        /// restarting it, when the edit didn't change the command structure. The
        /// player keeps its position, variables and call stack, the stage keeps its
        /// current background/actors, and the beat on screen is re-rendered so an
        /// edit to the visible line shows at once. Returns false when nothing is
        /// playing or the structure changed — the caller should then <see
        /// cref="Play"/> from the top.
        /// </summary>
        public bool TryHotSwap(string lvnJson)
        {
            if (!Playing) return false;
            LvnDocument doc;
            try { doc = LvnDocument.Parse(lvnJson); }
            catch { return false; }
            if (!_player.TryReplaceScript(doc)) return false;
            _cast = SpriteComposer.ParseCast(doc.Cast); // cast metadata is safe to refresh in place
            _player.RerenderCurrent();
            return true;
        }

        /// <summary>Wipe the stage to a clean slate NOW — actors, background, FX,
        /// dialogue. The host calls this when a chapter starts (before the script
        /// finishes downloading) so the previous chapter never lingers during the
        /// load, not only when the previous one ended.</summary>
        public void ClearStage()
        {
            if (!_built) return;
            ResetStage();
            _sayUp = false;
            _curChoices = null;
            _dialogue?.SetSpeaker(null);
            _dialogue?.SetText(string.Empty);
        }

        /// <summary>Persistent variables to preload into the next chapter BEFORE it
        /// runs (set by the host from its state store). With the imported global
        /// defaults marked `default:true`, these carried-in values survive the
        /// chapter's init block — so relationship/route/memory stats flow from one
        /// chapter to the next and across sessions.</summary>
        public Newtonsoft.Json.Linq.JObject SeedVars;

        /// <summary>Parse and start playing a .lvn document.
        /// <paramref name="warmIntroSpine"/>: pass false when a snapshot restore
        /// follows immediately (resume/load) — the intro warmup would otherwise
        /// build the CHAPTER-OPENING spine that the restore is about to discard,
        /// doubling the entry wait with a scene the player isn't even on.</summary>
        public void Play(string lvnJson, bool warmIntroSpine = true)
        {
            var doc = LvnDocument.Parse(lvnJson);
            // Хеш текста скрипта: по нему видно, ТА ЛИ ревизия главы играет —
            // без него живые правки контента не отличить от кэша в логе.
            string scriptHash;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                scriptHash = System.BitConverter.ToString(
                    sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(lvnJson ?? "")))
                    // НАРОЧНО по единицам: шестнадцатеричная запись — ASCII,
                    // пар в ней не бывает.
                    .Replace("-", "").Substring(0, 8).ToLowerInvariant();
            LvnPlayer.Log?.Invoke("════ PLAY scene=" + doc.Scene + " (" + (doc.Script?.Count ?? 0)
                + " cmds, скрипт " + scriptHash + ") ════");
            ExitRequested = false; // a fresh chapter is a fresh run
            _entryGateArmed = true; // the first say defers to the entry choreography
            _cast = SpriteComposer.ParseCast(doc.Cast);
            PrewarmGlyphs(doc); // rasterize the chapter's glyphs NOW, not mid-typewriter
            ResetStage();
            _player = new LvnPlayer(doc, this);
            _player.Strings = Strings; // localization catalog (text_id → string), if any
            if (SeedVars != null)      // carry stats in before the init defaults run
                foreach (var p in SeedVars.Properties()) _player.Vars[p.Name] = p.Value;
            _player.OnSay += RecordSay;
            ++_startGen;
            // warmIntroSpine=false ⇒ a RestoreSnapshot follows immediately and
            // advances via ContinueFrom. Running the intro here anyway (the old
            // behaviour) kicked the chapter-opening spine's build just to have
            // the restore reset it — the player watched the WRONG scene load
            // before their saved one.
            if (warmIntroSpine) StartWithSpineWarmup(_player, _startGen);
        }

        // The dialogue font is a DYNAMIC SDF asset — a glyph seen for the first
        // time rasterizes into the atlas on the render thread, a visible hitch in
        // the middle of a typewriter reveal. The chapter's full text corpus is
        // known here (script + localization catalog), so bake every distinct
        // character into the atlas up-front, behind the loading screen.
        private string _prewarmCorpus = ""; // kept so a late-arriving theme font warms too

        private void PrewarmGlyphs(LvnDocument doc)
        {
            var sb = new System.Text.StringBuilder(8192);
            if (doc?.Script != null)
                foreach (var c in doc.Script)
                {
                    if (!(c is JObject o)) continue;
                    sb.Append((string)o["text"]).Append((string)o["who"]);
                    if (o["options"] is JArray opts)
                        foreach (var opt in opts)
                            sb.Append((string)opt["text"]).Append((string)opt["cost"]);
                }
            if (Strings != null)
                foreach (var v in Strings.Values) sb.Append(v);
            _prewarmCorpus = sb.ToString();
            if (Theme != null && Theme.Font != null) // else: warms when the font arrives
                LvnFonts.Prewarm(Theme.Font, _prewarmCorpus);
        }

        // Bumped by every fresh start AND every snapshot restore, so a pending
        // intro warmup can tell its run was superseded. Pinning the player
        // reference alone is not enough: a resume REUSES the player Play just
        // created, and the stale warmup's Advance() would push the restored
        // chapter one beat past its saved position.
        private int _startGen;

        // The staged opening: everything the first beats show is built hidden
        // BEFORE the intro advances — the first Spine scene (skeleton build) AND
        // the plain art (background + character layers, decoded into the sprite
        // cache) warm in parallel behind the entry fade. Otherwise the
        // typewriter starts and then freezes mid-sentence while art decodes.
        // Capped: a dead network can't hold the intro hostage — whatever missed
        // the window loads on-demand exactly as before.
        private void StartWithSpineWarmup(LvnPlayer player, int gen)
            => LvnAsync.Fire(StartWithSpineWarmupAsync(player, gen), "StartWithSpineWarmup");

        private async Task StartWithSpineWarmupAsync(LvnPlayer player, int gen)
        {
            // Plain art warms in the BACKGROUND — it races the reader, never the
            // intro (holding the first beat hostage to 12 decodes read as a
            // multi-second black screen). Spine and the FIRST 3D set do gate:
            // otherwise skeleton/bundle work visibly freezes the first line.
            LvnAsync.Fire(WarmUpcomingArtAsync(12), "WarmUpcomingArt");
            try
            {
                await Task.WhenAll(
                    WarmUpcomingSpineAsync(12),
                    WarmUpcoming3DAsync(50));
            }
            catch (System.OperationCanceledException) { return; }
            catch { /* warmup is best-effort; the show path reloads what it needs */ }
            if (RunCurrent(player, gen)) player.Advance();
        }

        /// <summary>
        /// Wipe the stage to a clean slate before a chapter plays. Without this,
        /// actors, the background and effect veils left on screen by the previous
        /// chapter (or a live hot-reload) bleed into the new one — e.g. a character
        /// standing on the very first beat, before any <c>actor</c> command runs.
        /// </summary>
        // Bumped on every stage reset (chapter change / load). An async content
        // apply (bg, actor, spine, audio) captures it before its first await and
        // bails if it changed — otherwise a slow load from the PREVIOUS chapter
        // resolves after the reset and paints the new one (ghost actor, wrong bg,
        // wrong music). The shared _cts only cancels on OnDisable, not here.
        /// <summary>ХРОНОМЕТРИСТ — кто чего ждёт и чья работа устарела. Пять
        /// счётчиков порядка (эпоха, поколения актёра/фона/ожидания, два
        /// барьера) жили порознь в трёх файлах; правило одно, и живёт оно
        /// теперь в одном месте, проверяемом тестом без сцены.</summary>
        private readonly LvnStageClock _clock = new LvnStageClock();

        private int _stageEpoch => _clock.Epoch;

        /// <summary>True if <paramref name="epoch"/> is still the current stage
        /// generation — a content apply calls this after each await and stops
        /// touching the stage once it's stale.</summary>
        private bool StageCurrent(int epoch) => _clock.IsCurrent(epoch);

        /// <summary>Доедут ли команды <c>fx</c> до кадра. Полноэкранный стек
        /// живёт на КАМЕРЕ (OnRenderImage), и без неё команда уходит в никуда:
        /// диагностике важно отличать «не сработало» от «некому было
        /// работать».</summary>
        public bool FxAvailable => _renderer != null && _renderer.TryFx(new JObject());

        /// <summary>Дождаться, пока уходящие актёры доиграют свой уход — тот же
        /// приём, что у гардероба, где на сцене обязан остаться ровно один.</summary>
        public Task WaitForExitsAsync() => WaitForActorExitsAsync(_clock.Epoch);

        public void HandOver(JObject bg = null, string keepActor = null)
        {
            if (!_built) return;
            LvnLog.Trace($"[lvn-stage] HandOver → фон={(bg != null ? "новый" : "прежний")}, "
                       + $"остаётся={keepActor ?? "-"}");
            EndChapterFrame();
            // ДАННЫЕ ГЛАВЫ УХОДЯТ ВМЕСТЕ С НЕЙ — тот же список, что и звук.
            // Список открытых CG принадлежит НОВЕЛЛЕ, а не сцене: оставленный
            // на месте, он делал показ полотна витрины «показом картинки этой
            // новеллы», и хаб мог открыть чужую галерею просто тем, что игрок
            // вышел в меню. Каталог перевода и посевные статы тоже её: сцена
            // получает их заново на входе в следующую (DressStageAsync).
            Gallery = null;
            Strings = null;
            SeedVars = null;
            if (!string.IsNullOrEmpty(keepActor)) KeepActorAlive = keepActor;
            // ПО ТЕМ, КТО В КАДРЕ ИЛИ ЛЕТИТ В НЕГО. Показ асинхронный: актёр,
            // чьи слои ещё грузились, в список видимых не попадал — и всплывал
            // уже в меню, поверх витрины, посторонним гостем из прошлой главы.
            foreach (var id in ActorsInFrame())
                if (!string.Equals(id, keepActor, StringComparison.Ordinal))
                    HideActor(id, LvnSender.Menu);
            if (bg == null)
            {
                // Кадр остаётся тем же — трогать память НЕЛЬЗЯ. Открепить
                // спрайты значит разрешить кэшу выгрузить ровно то, что сейчас
                // на экране: сцена гасла в пустоту, а потом меню грузилось
                // заново, с задержкой.
                return;
            }
            ApplyStage(bg, LvnSender.Menu);
            // Новый фон уже поехал — прежний груз можно отпустить. Облик
            // остающегося это переживает (KeepActorAlive).
            UnpinAllSceneSprites();
            _prefetched.Clear();
        }

        /// <summary>
        /// ПЕРЕДАЧА КАДРА — глава кончилась, но сцена продолжается.
        ///
        /// <para>Меню — не отдельный экран, а состояние ЭТОЙ сцены: полотно оно
        /// ставит той же командой <c>bg</c>, куклу — той же <c>actor</c>. Разрыв
        /// был искусственный: выход из главы стирал сцену в ноль, а меню
        /// собирало её заново — с белым кадром на месте полотна и перезагрузкой
        /// слоёв героини. Отсюда же росли костыли вроде «держать арт куклы
        /// живым» и «страж самолечится».</para>
        ///
        /// <para>Здесь снимается только то, что принадлежало ГЛАВЕ: реплики,
        /// выборы, ввод, метки, эффекты, скип, ожидания. Декорация остаётся
        /// стоять, <paramref name="keepActor"/> остаётся стоять тоже — если он
        /// уже на сцене, его не трогают вовсе, и «героиня осталась» получается
        /// само собой, без единого перезагруженного слоя. Новый фон приходит
        /// обычной командой и потому меняется кроссфейдом, а не через
        /// черноту.</para>
        /// </summary>
        private void EndChapterFrame()
        {
            _clock.NewEpoch(); // работа прошлой сцены теряет право рисовать (и барьеры с ней)
            // ЗВУК УХОДИТ С ГЛАВОЙ ЦЕЛИКОМ. Здесь снимали только луп печати, а
            // музыка и эмбиент оставались играть — и в меню их слышно поверх
            // витринного трека («выходишь из главы, музыка дублируется»).
            // Список того, что уносит уходящая глава, живёт в ЭТОМ методе, и
            // звук в нём был неполон.
            _audio?.SilenceChapter();
            // Close the quick menu FIRST: it may be mid-open (IsOpen + InputBlocked
            // set, its clean-frame screenshot coroutine pending). The StopAllCoroutines
            // below would kill that coroutine before its OpenSheetChrome callback,
            // stranding InputBlocked=true forever — a soft-lock. Close() resets both.
            _menu?.Close();
            // Kill any in-flight `wait` coroutine — it reads the _player field, so
            // after Play() swaps in a new player it would otherwise fire Advance()
            // on the fresh chapter when its old timer elapses.
            StopAllCoroutines();
            _hotspots.Clear();
            // A resume veil (1/255 alpha) left by an aborted restore must not
            // black out the NEXT chapter — reset it at every scene boundary.
            if (_renderer is CanvasSceneRenderer resetCanvas && resetCanvas.Root != null)
            {
                var g = resetCanvas.Root.GetComponent<CanvasGroup>();
                if (g != null) g.alpha = 1f;
            }
            // A story panel (wardrobe sheet…) left open across a chapter change
            // would float over the new scene — dismiss it with the old one.
            if (_panelHost != null) LvnAsync.Fire(_panelHost.HideAsync(), "Hide");
            _talkAnims.Clear();
            _particles?.Set("rain", false);
            _particles?.Set("snow", false);
            _fx?.Clear(0f);
            _fx?.ClearBlur(0f);
            _backlog.Clear();
            _prefetched.Clear(); // the next chapter/load re-warms from scratch
            ShowChromeAll(); // скрытый интерфейс не переживает сцену, что бы его ни держало
            StopSkip();             // fast-forward dies with the scene it was skipping
            _awaitingTap = false;
            _awaitingWait = false;
            _sayUp = false;
            SetSayVisible(false);
            _curChoices = null;
            StopChoiceTimer();
            CloseInput();
            _awaitingInput = false;
            _audio?.StopVoice();
            _draggables.Clear();
            _spokenIds.Clear();
            _soloHidden.Clear();
            _dragId = null;
            _dragCandidate = null;
            _choices?.Dismiss(); // clear any on-screen choice buttons (avoid stale clicks)
            _labelLayer?.Clear();
            _uiLayer?.Clear();   // деревья `ui` — того же срока жизни, что метки
            _labels.Clear();
            ForgetHint();   // части подсказки оторваны Clear'ом выше
        }

        private void ResetStage()
        {
            // Кто и когда стирает сцену — ключ к «белому полотну после главы»:
            // уборка, пришедшая ПОСЛЕ постановки меню, снимает его фон.
            LvnLog.Trace($"[lvn-stage] ResetStage → epoch={_stageEpoch}\n{StackTraceUtility.ExtractStackTrace()}");
            EndChapterFrame();
            // ── дальше — то, что уборка сносит, а передача кадра оставляет ──
            //
            // ТЕМП ПЕЧАТИ — НАСТРОЙКА ГЛАВЫ, А НЕ ИГРЫ. `text_pace` пишет
            // статическое поле ядра, и сбрасывать его было некому: медленная
            // драматичная сцена замедляла следующую главу, а через меню — и
            // ЧУЖУЮ новеллу. Автор второй новеллы искал бы причину у себя и не
            // нашёл: в его сценарии про темп не сказано ни слова.
            //
            // Ноль значит «как в настройках игрока» (см. TypewriterClock).
            Lvn.TypewriterClock.GlobalCps = 0f;
            HasBackdrop = false;
            _lastBgCmd = null; // новая сцена применяет свой первый bg безусловно
            // ГЕРОИНЯ ОСТАЁТСЯ ЖИВОЙ. Она одна и та же по обе стороны перехода;
            // раньше уборка её убивала, и по ту сторону собиралась вторая —
            // отсюда «героинь две» и провал, пока новая грузит свои слои.
            if (_renderer != null) _renderer.KeepAlive = KeepActorAlive;
            _renderer?.RemoveAll();
            _renderer?.ResetCamera(0f);
            // ПОЛОТНО ОСТАЁТСЯ. Снять его — значит показать чёрный кадр, пока
            // новая сцена качает свой фон; в кадре всегда должно что-то быть.
            // Трёхмерная подложка — другое дело: она принадлежит сцене целиком
            // и без своих объектов бессмысленна.
            _renderer?.ClearBackground();
            // Память фона держится до нового кадра ровно по той же причине:
            // открепить сейчас значит разрешить кэшу выгрузить то, что игрок
            // видит на экране. Актёров и прочее отпускаем.
            UnpinAllSceneSprites();
            ReleaseActive3DSet();
            // ПАМЯТЬ УХОДИТ ВСЯ — включая героиню, хотя её объект остаётся жить.
            // Это разные вещи, и путать их нельзя: живой объект избавляет от
            // пересборки, а память о позе — липкая. Команда, которой её ставило
            // МЕНЮ (центр, рост витрины), пережила старт главы и подмешивалась
            // к авторской: героиня выходила в сцену стоящей по-менюшному —
            // «начинаешь с сохранения, а она не встраивается в игру, хотя её
            // реплика» (Илья, 27.08). Глава обязана ставить её с чистого листа.
            // При ВЫХОДЕ в меню память нужна и цела: там уборки нет вовсе,
            // кадр передаётся через HandOver.
            _memory.ForgetPoses();
            // А ВОТ «ЧТО НА КОМ НАДЕТО» (_actorLook) ЗДЕСЬ НЕ СТИРАЕТСЯ, и это
            // намеренно. Память о позе — договор истории с собой, она уходит с
            // главой; облик же — свойство самой фигуры. Героиня переживает
            // уборку живой (KeepActorAlive), и её слои уже надеты: стереть эту
            // запись значило бы заставить её собираться заново на выходе из
            // главы — тот самый «шум, белое пятно и бац». У тех, чьи фигуры
            // уборка снесла, запись безвредна: ActorArtAlive скажет правду.
            // КАДР ИСТОРИИ УХОДИТ ВМЕСТЕ СО СВОЕЙ ГЛАВОЙ. Оставить его — значит
            // дать следующей сцене вернуться к людям, которых в ней нет: запись
            // прошлого кадра описывает прошлую историю, и «восстановить» её в
            // новой главе было бы точным исполнением бессмыслицы.
            StoryFrame.Actors.Clear();
            StoryFrame.Background = null;
            StoryFrame.Veil = null;
            foreach (var kv in _skeletons) if (kv.Value.Go != null) Destroy(kv.Value.Go);
            UnpinAllSpinePages(); // release page-texture pins so the LRU can reclaim them
            // Одна запись — одна уборка. Раньше здесь стояли три Clear подряд,
            // и четвёртая память (место в порядке давности) в них не входила.
            _skeletons.Clear();
            _spineMru.Clear();
        }

        private void RecordSay(string who, string text, string style)
        {
            // After a rollback, the restored beat re-runs and would duplicate its
            // own backlog entry — swallow exactly that one repeat.
            if (_suppressDupSay)
            {
                _suppressDupSay = false;
                if (_backlog.Count > 0)
                {
                    var last = _backlog[_backlog.Count - 1];
                    if (last.who == who && last.text == text) return;
                }
            }
            _backlog.Add((who, text, style));
            // Read tracking: remember the line, and if fast-forward is in
            // read-only gear, stop it the moment something NEW comes up — the
            // line stays on screen with its typewriter, exactly where the
            // player's actual reading resumes.
            bool wasNew = LvnReadStore.MarkRead(_saveTitleId, who, text);
            if (wasNew && Skipping && LvnPrefs.SkipReadOnly) StopSkip();
            // Rolling autosave so a crash mid-scene loses a few lines at most.
            if (++_saySinceAutosave >= AutosaveEveryLines)
            {
                _saySinceAutosave = 0;
                AutosaveNow();
            }
        }

        private bool _suppressDupSay;

        /// <summary>True when there is a previous beat to roll back to.</summary>
        public bool CanRollback => _player != null && _player.CanRollback && !_awaitingWait;

        /// <summary>Step one beat back (a mis-tap safety net): restore the previous
        /// say/choice's snapshot — variables as they were BEFORE it ran, so a picked
        /// option's set/inc is undone — rebuild the scene there and re-show it.
        /// Returns false when already at the first beat.</summary>
        public bool RollbackStep() => RollbackSteps(1);

        /// <summary>Roll back several beats in one hop (clamped to the recorded
        /// history) — the History panel's tap-to-return. The same recipe as a
        /// single step, but one scene rebuild instead of N.</summary>
        public bool RollbackSteps(int steps)
        {
            if (_player == null || _awaitingWait || steps < 1) return false;
            int actual = Mathf.Min(steps, _player.HistoryDepth - 1);
            var snap = _player.PopRollback(actual);
            if (snap == null) return false;

            // ResetStage wipes the dialogue history; a rewind must keep it minus
            // the beats being undone (their re-runs are dedup'd in RecordSay).
            var kept = new List<(string who, string text, string style)>(_backlog);
            for (int i = 0; i < actual; i++)
            {
                if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
                // A trailing choice mark belongs to the pick being undone: the
                // options re-present and the (re-)pick records a fresh mark.
                while (kept.Count > 0 && kept[kept.Count - 1].style == "choice")
                    kept.RemoveAt(kept.Count - 1);
            }

            ResetStage();
            _backlog.AddRange(kept);
            _suppressDupSay = true;

            _player.Restore(snap);
            int at = _player.Index;
            _player.ReplayVisuals(at);
            _player.ContinueFrom(at);
            return true;
        }
    }
}
