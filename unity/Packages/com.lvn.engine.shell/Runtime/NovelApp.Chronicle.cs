using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЛЕТОПИСЕЦ — записывает, что произошло с историей и с игроком.
    ///
    /// <para>Метки автора — это «слайды»: по ним видно, до какого места дошли и
    /// где отваливаются. Показ вариантов и выбор — места, где решает игрок:
    /// сколько вариантов написано, сколько показано (условия отсеивают часть) и
    /// сколько времени человек думал. Без этой записи между входом в главу и
    /// выходом ПУСТО, и на вопрос «где мы теряем людей» отвечать нечем.</para>
    ///
    /// <para>Отдельным домом, потому что летопись — сквозная тема: она слушает
    /// плеер, а не принадлежит ни одной его функции, и её подписки живут
    /// столько же, сколько приложение. Каждая подписка сперва СНИМАЕТСЯ: Start
    /// у долгоживущего объекта может пройти дважды (пересоздание оболочки), и
    /// без снятия одно событие обрабатывалось бы дважды — а по такой летописи
    /// потом считают воронку.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
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
            // «Файл доехал, а картинкой не стал» — та же для игрока пропажа, что
            // и недоехавший ассет, поэтому и событие то же. Раньше этот случай
            // не сообщался никак: показ ловил исключение и оставлял силуэт.
            Lvn.Content.ContentLoader.AssetUnusable -= OnAssetUnusable;
            Lvn.Content.ContentLoader.AssetUnusable += OnAssetUnusable;

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

        private static void OnLabelReached(string label, int at)
        {
            if (string.IsNullOrEmpty(label)) return;
            lock (_reachedLabels)
            {
                if (_reachedLabels.Count > 500) _reachedLabels.Clear(); // без роста без предела
                if (!_reachedLabels.Add(label)) return;
            }
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.LabelReach, ("label", label), ("at", at));
            // Та же позиция нужна отзыву: «тут баг» без места в сценарии
            // невоспроизводим, а сам игрок место назвать не может.
            Lvn.Services.LvnWhereabouts.Mark(label, at);
        }

        private static void OnChoiceShown(int written, int shown, int at)
            => Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChoiceShown,
                ("written", written), ("shown", shown), ("at", at));

        private static void OnChoicePicked(int index, string text, float seconds, int at)
            => Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChoicePick,
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
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.UnknownOp,
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

            EnterChapterContext(title, chapter);
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
                await Lvn.Content.LvnGlobalStats.OverlayAsync(_state, Stage.SeedVars);
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
                    // Переток «надетое → сюжетные переменные» ведёт СВЯЗНОЙ:
                    // обратная сторона того же обряда живёт при открытии листа,
                    // и порознь они разъезжались.
                    Lvn.UI.LvnWardrobeSync.ToVars(kv.Key, kv.Value?.wardrobe,
                        (name, val) => SetVarPath(Stage.SeedVars, name, val));
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
            Lvn.UI.LvnPlayerName.Seed(Stage.Player, playerName);

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
                // Живые статы читаем ДО восстановления: наложение ниже успевает
                // до асинхронных продолжений восстановления, и условия
                // возобновлённого шага видят свежие значения.
                var freshGlobal = await Lvn.Content.LvnGlobalStats.LoadAsync(_state);
                Stage.RestoreSnapshot(autosave.Snap);
                Lvn.UI.LvnPlayerName.Seed(Stage.Player, playerName);
                if (freshGlobal != null && freshGlobal.Count > 0 && Stage.Player != null)
                    Stage.Player.Vars[Lvn.Content.LvnGlobalStats.VarName] = freshGlobal;
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
            // Полосу GameHud убрали 26.08 (её работу взял единый навбар), но
            // кормление осталось: статы, прогресс и балансы шли в экран,
            // который НИКОГДА не показывается — Show(Hud) нет ни в одном месте.
            // Мёртвая работа на каждый шаг главы и на каждое движение кошелька.
            while (Stage.Player != null && !Stage.Player.Finished && !Stage.ExitRequested
                   && !destroyCancellationToken.IsCancellationRequested)
            {
                _shell.TopBar?.SetProgress(Stage.Player.ProgressIndex, Stage.Player.ProgressTotal);
                await Task.Yield();
            }
            bool exited = Stage.ExitRequested;
            Stage.ClearExitRequest();
            // Вышли из главы — кадр ПЕРЕХОДИТ меню, а не стирается: полотно
            // меняется кроссфейдом, героиня остаётся стоять в том наряде и с
            // той эмоцией, с какими кончилась глава.
            // ПЕРЕХОД В МЕНЮ ЗДЕСЬ НЕ ИГРАЕТСЯ: он один на все пути выхода и
            // живёт там, где кончается цикл глав (NovelApp.Chapter). Второй
            // вызов отсюда проигрывал его ДВАЖДЫ — второй раз по уже пустой
            // сцене.
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
            _shell.TopBar?.SetProgress(1, 1);
            // The chapter that actually played to the end — a cross-chapter save
            // load may have switched the stage away from the requested one.
            bool finished = Stage.Player != null && Stage.Player.Finished;
            var played = _currentChapter ?? chapter;
            LeaveChapterContext();
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
    }
}
