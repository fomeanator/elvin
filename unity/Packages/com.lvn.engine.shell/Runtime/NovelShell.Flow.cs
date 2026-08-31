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
    /// ПОТОК ИГРЫ — часть <see cref="NovelShell"/>: главный цикл оболочки.
    /// Показать хаб, дождаться выбора игрока, отдать управление главе, принять
    /// его обратно — и решить, не пора ли сначала проиграть интро.
    ///
    /// <para>Цикл длинный и с ветками (интро, продолжение, возврат из главы,
    /// смена новеллы), и читать его нужно целиком, а не выкапывать из файла,
    /// где рядом лежит сборка панелей и атмосфера меню.</para>
    /// </summary>
    public sealed partial class NovelShell
    {
        /// <summary>Run the whole loop. <paramref name="bootReady"/> gates the boot
        /// splash; <paramref name="chapterReady"/> (optional) gates each chapter's
        /// loading bar; <paramref name="playChapter"/> plays the chosen chapter and
        /// returns when it finishes. Loops back to the carousel after each chapter.</summary>
        public async Task RunAsync(
            Func<bool> bootReady = null,
            Func<LvnChapter, Func<bool>> chapterReady = null,
            Func<LvnChapter, Func<float>> chapterProgress = null,
            Func<LvnTitle, LvnChapter, string, Task> playChapter = null,
            bool askName = true,
            CancellationToken ct = default,
            Func<float> bootProgress = null,
            bool bootSplash = true)
        {
            if (_root == null) throw new InvalidOperationException("Call Build() before RunAsync().");

            Boot.Hide();
            ShowOnly(); // hide all
            // ── boot splash ──
            // bootSplash=false: the host's own boot surface (NovelApp's engine
            // veil) already covers this wait — showing a SECOND loading screen
            // under it would flash a second bar at the hand-off. Wait silently.
            if (bootSplash)
            {
                Show(Boot);
                await Boot.RunAsync(bootReady ?? (() => true), bootProgress, ct);
                Hide(Boot);
            }
            else
            {
                var ready = bootReady ?? (() => true);
                while (!ready() && !ct.IsCancellationRequested)
                    await Task.Yield();
                if (ct.IsCancellationRequested) return;
            }

            // The player's name persists across launches — nobody re-asks it.
            // Имя спрашивают у дома по месту — копировать его в поле незачем.

            // ── welcome/auth screen: the FIRST launch only ──
            // Later launches go straight in; the device sign-in runs silently
            // either way. A nickname entered here seeds the player name.
            // ВВОДНАЯ ГЛАВА ИДЁТ ПЕРВОЙ И БЕЗ ВОПРОСОВ. Пока она не пройдена, у
            // игрока не спрашивают ни имени, ни новеллы: он попадает прямо в
            // историю, а она сама и знакомится, и объясняет правила. Витрина
            // ждёт своей очереди — см. IntroTitle ниже.
            var introTitle = PendingIntroTitle();
            if (Auth != null && !Lvn.UI.LvnPrefs.SeenWelcome && introTitle == null)
            {
                try
                {
                    var nick = await Auth.AskAsync(ct);
                    Lvn.UI.LvnPrefs.SeenWelcome = true;
                    if (!string.IsNullOrEmpty(nick))
                    {
                        Lvn.UI.LvnPlayerName.Set(nick);
                    }
                }
                catch (OperationCanceledException) { return; }
            }

            while (!ct.IsCancellationRequested)
            {
                // ПОКАЗАЛ — ОБЯЗАН СНЯТЬ, чем бы виток ни кончился. Раньше
                // подстраховка стояла хвостом, и до неё не доходили: отмена
                // посреди главы уходила `return`-ом мимо, оставляя НЕПРОЗРАЧНЫЙ
                // экран загрузки поверх меню. Игрок видел заставку, под которой
                // работает живой интерфейс, и не мог ничего нажать.
                try
                {
                    // ── choose a title: hub flow or the carousel ──
                    LvnTitle title;
                    var intro = PendingIntroTitle();
                    if (intro != null)
                    {
                        title = intro;   // выбора нет — и это намеренно
                    }
                    else if (Browse != null)
                    {
                        // ВПУСКАЕТ ШВЕЙЦАР. Цикл только называет участников и
                        // условие «дверь закрыта»; порядок (зарядить → дождаться →
                        // показать → двинуть) и предохранители — его забота.
                        // Витрина на экране — значит глава не идёт; вид доведут подписчики.
                        Lvn.UI.LvnScreenDirector.Current.AnnounceChapter(false);
                        LvnAsync.Fire(LvnUsher.OpenAsync(
                            hold: () => BootVeil.IsVisible,
                            show: () => Show(Browse.View),
                            Browse as ILvnEntrance, TopBar), "ShellEntrance");
                        OnMenuVisible?.Invoke(); // сцена меню ставится ПО ФАКТУ показа
                        // ОХОТА НА БЕЛЫЙ ПРЯМОУГОЛЬНИК (26.08): сцена по логам
                        // ставит и полотно, и куклу — значит светлое пятно рисует
                        // сама оболочка. Через секунду после показа перечисляем
                        // ВСЕ крупные светлые непрозрачные поверхности дерева.
                        if (Lvn.UI.LvnLog.Verbose)
                            _root?.schedule.Execute(DumpOpaqueSurfaces).ExecuteLater(1200);
                        // ОДИН ВОПРОС — ОДИН ОТВЕТ. Как витрина его получит
                        // (карточка, карусель, защёлкнутая ссылка) — её дело.
                        title = await Browse.PickTitleAsync(ct);
                        // (вход полос играет сам по себе — ждать его незачем)
                        if (ct.IsCancellationRequested) return;
                        Hide(Browse.View);
                        if (title == null) continue; // ушёл, не выбрав → витрина заново
                    }
                    else
                    {
                        return; // витрины нет — показывать нечего
                    }
                    // "Играть" continues from the furthest STARTED chapter (started
                    // ch2 → the button opens ch2); a fresh/finished title starts at
                    // chapter one. PlayChapterAsync applies the same resume rule —
                    // resolving it HERE too makes the loading screen show the right
                    // chapter's backdrop and preload the right asset plan.
                    var chapter = LvnProgress.Current(title) ?? FirstChapter(title);

                    // The name ask lives INSIDE the chapter entry now (after the
                    // title card, over the live scene) — the host owns it.

                    // ── chapter loading (Liminal-style entry) ──
                    // The loader stays OPAQUE while the chapter boots BEHIND it —
                    // the host fades it out via RevealFromLoadingAsync() once the
                    // scene has its first background, then floats the chapter title
                    // over the LIVE scene (ShowChapterTitleAsync). No frame of raw
                    // stage ever shows between screens.
                    var ready = chapterReady?.Invoke(chapter) ?? (() => true);
                    var prog = chapterProgress?.Invoke(chapter);
                    bool cached = ready();
                    if (Portal != null)
                    {
                        // ПЕРЕХОД БЕЗ ЭКРАНА. Игрок уже нажал «играть» — между его
                        // решением и историей не должно быть ещё одной остановки.
                        // Створ стоит на главной, героиня уходит в него, и следом
                        // кадр забирает глава.
                        if (OnPortalEnter != null) await OnPortalEnter();
                    }
                    else
                    {
                        Show(Loading);
                        await Loading.RunAsync(ready, prog, ct, bgUrl: chapter?.bg_url,
                            minSecondsOverride: cached
                                ? (Transitions?.loading_floor ?? 0.25f)
                                : (float?)null);
                    }

                    // ── play ──
                    if (playChapter != null && chapter != null)
                    {
                        LvnAsync.Fire(Lvn.Services.LvnWallet.NudgeAsync(), "Refresh"); // свежие баблики к игре
                        // Полоса GameHud удалена (решение Ильи 26.08): затемнение
                        // сверху убрано, прогресс и валюта живут МИНИ-БАБЛИКАМИ
                        // единого навбара по углам сцены.
                        BeginChapterSession();   // один дом начала: хром, лента, режим, хост
                        try { await playChapter(title, chapter, _playerName); }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex) { Debug.LogWarning($"[shell] chapter play failed: {ex.Message}"); }
                        EndChapterSession();   // один дом завершения: хром, режим, полоса, хост
                    }
                    // Вводная считается пройденной, когда доиграна до конца: бросил
                    // на середине — при следующем запуске снова попадёт в неё, а не
                    // на витрину, которую ещё не заслужил.
                    if (intro != null && IsTitleFinished(intro)) Lvn.UI.LvnPrefs.IntroDone = true;

                }   // конец витка: дальше — уборка, что бы ни случилось
                finally
                {
                    Loading.Hide();
                    Title.Hide();
                    if (BootVeil.IsVisible) BootVeil.Hide(); // и брендовую вуаль первого входа
                }
            }
        }

        /// <summary>Первый вход ещё впереди (вводная не пройдена): хост держит
        /// брендовую вуаль вместо полос — см. NovelApp.DriveBootVeilAsync.</summary>
        public bool HasPendingIntro => PendingIntroTitle() != null;

        private LvnTitle PendingIntroTitle()
        {
            if (Lvn.UI.LvnPrefs.IntroDone)
            {
                Debug.Log("[lvn-intro] ворота: IntroDone=true (метка устройства) — витрина");
                return null;
            }
            if (_manifest?.titles == null) return null;
            foreach (var t in _manifest.titles)
                if (t != null && string.Equals(t.type, "intro", StringComparison.OrdinalIgnoreCase))
                {
                    bool done = IsTitleFinished(t);
                    // Диагностический след: «почему не стартанула воронка» иначе
                    // выясняется раскопками PlayerPrefs на чужом устройстве.
                    Debug.Log($"[lvn-intro] ворота: '{t.id}' reached={LvnProgress.Reached(t)} "
                        + $"current={(LvnProgress.Current(t)?.id ?? "-")} → "
                        + (done ? "пройдена, витрина" : "играем воронку"));
                    return done ? null : t;
                }
            Debug.Log("[lvn-intro] ворота: intro-тайтла в манифесте нет — витрина");
            return null;
        }

        /// <summary>Новелла пройдена? Ответ у прогресса — правило одно на всех
        /// (см. <see cref="LvnProgress.Finished"/>).</summary>
        private static bool IsTitleFinished(LvnTitle t) => LvnProgress.Finished(t);

        /// <summary>Открыть новеллу по id, не гоняясь с заставкой: запрос
        /// исполнится в тот миг, когда витрина возьмёт управление. false — новеллы
        /// с таким id нет. Спрашивает ВИТРИНУ (<see cref="ILvnBrowse.RequestTitle"/>):
        /// раньше запрос уходил в карусель — и в режиме хаба ссылка не делала
        /// ничего, отвечая при этом «сделано».</summary>
        public bool RequestPlay(string titleId) => Browse?.RequestTitle(titleId) ?? false;


        /// <summary>The first playable chapter of a title (lowest non-negative
        /// chapter number across its seasons), or null.</summary>
        /// <summary>Первая глава новеллы — правило у ПРИВРАТНИКА (наименьший
        /// номер): здесь была копия, а в хосте лежала третья версия с другим
        /// правилом.</summary>
        internal static LvnChapter FirstChapter(LvnTitle title)
            => Lvn.Content.LvnGatekeeper.First(title);

        // ТИТР ГЛАВЫ — только номер: название эпизода стоит в титре отдельной
        // строкой, и дублировать его в подзаголовке незачем.
        private static string ChapterLine(LvnChapter c) => Lvn.Content.LvnCaptions.ChapterNumberOnly(c);

        /// <summary>Подпись главы для сцены перехода: имя эпизода, иначе его
        /// номер. Пустая строка честнее выдуманного заголовка.</summary>
        private static string PortalChapterLabel(LvnChapter c) => Lvn.Content.LvnCaptions.Chapter(c);

    }
}
