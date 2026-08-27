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
            var resume = LvnProgress.Current(title);
            // Computed BEFORE any SetCurrent: a novel-fresh start (first ever
            // play, or a post-finale replay) re-asks the player's name inside
            // the first chapter's entry.
            bool novelFreshStart = resume == null;
            if (resume != null) chapter = resume;
            // A COMPLETED novel replays clean: Current is cleared on the finale
            // but the title-scope vars still hold the whole playthrough — route
            // the fresh entry through the restart machinery so chapter one seeds
            // from its pristine entry checkpoint, not from endgame stats.
            if (resume == null && LvnProgress.Reached(title) > 0 && chapter != null)
                LvnProgress.RequestRestart(title?.id, chapter.id);
            // Resuming a chapter the player already paid to enter must not charge
            // again. "Already entered" = ITS autosave exists (written at entry) —
            // the progress marker alone isn't enough: finishing a chapter moves
            // the marker to the NEXT one, and that entry hasn't been paid yet.
            var entrySlot = LvnSaveStore.Get(title?.id, LvnSaveStore.AutoSlot);
            bool alreadyEntered = resume != null && entrySlot?.Snap != null
                && entrySlot.Snap.ScriptUrl == resume.script_url
                && !entrySlot.Snap.Finished;
            while (chapter != null)
            {
                // The script must be REACHABLE before anything is charged — an
                // offline entry used to burn the energy and silently bounce to
                // the menu (and charge AGAIN on the retry).
                if (!await EnsureChapterScriptAsync(chapter))
                {
                    var eco = _manifest?.economy;
                    await _shell.AlertAsync(eco?.gate_title ?? LvnOfflineText.Title,
                        "Глава недоступна без сети. Проверь подключение и попробуй ещё раз.");
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
                    _chapterSched = _downloads.BeginChapter(chapter, destroyCancellationToken);
                _preparedChapter = null;
                LvnProgress.SetCurrent(title, chapter);
                // Пока игрок внутри новеллы, каждое событие обязано знать, в
                // какой именно: без этого сбой не отнести к истории, а таких
                // событий в отчёте больше половины.
                Lvn.Services.LvnAnalytics.CurrentTitle = title?.id;
                Lvn.Services.LvnAnalytics.CurrentChapter = chapter.id;
                lock (_reachedLabels) _reachedLabels.Clear(); // воронка считается ПО ГЛАВЕ
                SyncProgressVault(); // every progress move lands in all three homes
                ChapterStarted?.Invoke(title, chapter);
                Lvn.Services.LvnAnalytics.Track("chapter_start",
                    ("title", title?.id), ("chapter", chapter.id));
                var finished = await PlayOneChapterAsync(title, chapter, playerName, novelFreshStart);
                novelFreshStart = false; // only the entry chapter of this run counts
                if (finished == null)
                {
                    // Уход ИЗ СЕРЕДИНЫ главы. Без этого события потеря внутри
                    // главы выводилась вычитанием (start минус finish), и в
                    // одно число сливались крах, гибель, упёршийся в энергию и
                    // просто заскучавший. Позиция говорит, ГДЕ бросили: у
                    // «дочитал до середины и вышел» и «вылетело на первом
                    // кадре» разные причины и разные починки.
                    // Контекст КАДРА, а не только позиции. Иначе «ушли на
                    // команде 137» не отвечает ни на что: половина глав вообще
                    // без выборов, и бросают там не из-за развилки, а из-за
                    // того, ЧТО на экране — плохой спрайт персонажа, не тот
                    // фон, зависшая сцена. Метка + фон + кто на сцене дают
                    // место, которое можно открыть и посмотреть глазами.
                    var snap = Stage?.Player?.Save();
                    Lvn.Services.LvnAnalytics.Track("chapter_abandon",
                        ("title", title?.id), ("chapter", chapter.id),
                        ("at", Stage?.Player?.Index ?? -1),
                        ("label", snap?.AnchorStableLabel ?? snap?.AnchorLabel),
                        ("bg", Lvn.UI.VnStage.LastSceneBgUrl),
                        ("actors", Stage?.ActorsOnStage()));
                    FlushUnknownOps(title, chapter);
                    break; // → carousel
                }
                ChapterFinished?.Invoke(title, finished);
                Lvn.Services.LvnAnalytics.Track("chapter_finish",
                    ("title", title?.id), ("chapter", finished.id));
                FlushUnknownOps(title, finished);
                // A cross-chapter save load can land the player in another title —
                // continue along whichever title the finished chapter belongs to.
                var (owner, _) = FindChapterByScriptUrl(finished.script_url);
                if (owner != null) title = owner;
                var next = NextChapterOf(title, finished);
                // The FINISH is what advances progress — not the «Дальше» tap.
                // Leaving via the chapter-end menu used to strand the marker on
                // the finished chapter, and «Играть» replayed it from the top.
                if (next != null)
                    LvnProgress.SetCurrent(title, next);
                else
                {
                    LvnProgress.ClearCurrent(title); // the novel is complete — replays restart
                    // ВОРОНКА ПРОЙДЕНА — ПРЯМО ЗДЕСЬ, ФАКТОМ ФИНАЛА. Ворота в
                    // оболочке выводили это из reached/Current и на живом
                    // устройстве промахивались — партнёр получил «пролог по
                    // кругу» на чистой установке. Финал последней главы
                    // вводной — единственный надёжный свидетель.
                    if (string.Equals(title?.type, "intro", StringComparison.OrdinalIgnoreCase))
                    {
                        Lvn.UI.LvnPrefs.IntroDone = true;
                        Debug.Log("[lvn-intro] вводная доиграна до конца — витрина открыта");
                    }
                }
                SyncProgressVault();
                // Between-chapters screen (ui.chapter_end): "Конец главы" with
                // continue/menu. Without it chapters flow seamlessly, as before.
                if (_shell?.ChapterEnd != null)
                {
                    bool goNext = await _shell.ChapterEnd.ShowAsync(finished.name, hasNext: next != null);
                    if (!goNext || next == null) break;
                }
                else if (next == null) break;
                chapter = next;
            }
            // Back to the menu — stop the chapter scheduler so its deferred
            // downloads don't keep competing with the menu's own refresh.
            _downloads?.EndChapter();
            _chapterSched = null;
            // Вышли из новеллы: события меню не должны числиться за историей,
            // из которой игрок уже ушёл.
            Lvn.Services.LvnAnalytics.CurrentTitle = null;
            Lvn.Services.LvnAnalytics.CurrentChapter = null;
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

        // Preflight: make the chapter's script locally available (cache hit or
        // a live fetch) BEFORE the entry charge — money never burns on a
        // chapter that can't start. The later fetch inside PlayOneChapterAsync
        // then hits the cache.
        private async Task<bool> EnsureChapterScriptAsync(LvnChapter chapter)
        {
            if (chapter == null || string.IsNullOrEmpty(chapter.script_url)) return false;
            if (_assets.Loader.IsScriptCached(chapter.script_url)) return true;
            try
            {
                var json = await _assets.Loader.DownloadScriptCached(chapter.script_url);
                return !string.IsNullOrEmpty(json);
            }
            catch { return false; }
        }

        // Background full-library warm: чей-то экран загрузки всегда важнее —
        // the loop parks while a chapter scheduler is actively gating.
        private async Task WarmLibraryAsync(LvnManifest manifest, System.Threading.CancellationToken ct)
        {
            try
            {
                await Task.Delay(3000, ct); // let the boot/menu settle first
                int warmed = 0, skipped = 0;
                if (manifest?.titles != null)
                    foreach (var t in manifest.titles)
                    {
                        if (t?.seasons == null) continue;
                        foreach (var se in t.seasons)
                        {
                            if (se?.chapters == null) continue;
                            foreach (var ch in se.chapters)
                            {
                                if (ch == null) continue;
                                if (!string.IsNullOrEmpty(ch.script_url) && !_assets.Loader.IsScriptCached(ch.script_url))
                                    try { await _assets.Loader.DownloadScriptCached(ch.script_url); } catch { }   // разбор объявленных переменных: кривой блок не должен ронять главу
                                if (ch.assets == null) continue;
                                foreach (var kv in ch.assets)
                                {
                                    if (ct.IsCancellationRequested) return;
                                    var url = kv.Key;
                                    if (string.IsNullOrEmpty(url)) continue;
                                    // an active chapter gate owns the bandwidth
                                    while (_chapterSched != null && !_chapterSched.AllDone && !ct.IsCancellationRequested)
                                        await Task.Delay(500, ct);
                                    // …and so does anything a LIVE surface is
                                    // waiting to draw right now: an actor
                                    // mid-scene must never queue behind next
                                    // week's chapters.
                                    while (_assets.LivePressure > 0 && !ct.IsCancellationRequested)
                                        await Task.Delay(150, ct);
                                    if (Lvn.Content.LvnNetworkStatus.IsOffline)
                                    { await Task.Delay(3000, ct); continue; }
                                    if (_assets.Loader.IsAssetCached(url)) { skipped++; continue; }
                                    try { await _assets.Loader.DownloadAssetBytes(url, ct); warmed++; }
                                    catch (System.OperationCanceledException) { return; }
                                    catch { /* self-heal covers per-file failures */ }
                                }
                            }
                        }
                    }
                LvnLog.Trace($"[lvn-warm] library fully cached ({warmed} fetched, {skipped} already local)");
            }
            catch (System.OperationCanceledException) { /* teardown */ }
        }
    }
}
