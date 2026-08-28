using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Per-title reading progress, shared by the play loop (auto-continue) and
    /// the carousel (the Continue label + the chapter picker):
    /// <list type="bullet">
    ///   <item><b>Current</b> — the chapter the player is in / left off in.
    ///   Moves freely (forward on chapter transitions, anywhere on a save load).</item>
    ///   <item><b>Reached</b> — the furthest chapter number ever STARTED. Only
    ///   ever goes up, so replaying an early chapter never re-locks later ones
    ///   in the picker.</item>
    /// </list>
    /// PlayerPrefs-backed, like the save slots.
    /// </summary>
    public static class LvnProgress
    {
        private static string CurKey(string titleId) => "lvn_chapter_" + (titleId ?? "");
        private static string CurNumKey(string titleId) => "lvn_chapter_num_" + (titleId ?? "");
        private static string ReachedKey(string titleId) => "lvn_reached_" + (titleId ?? "");

        /// <summary>Record that the player is (now) in this chapter. Bumps
        /// Reached when this is the furthest chapter so far.</summary>
        // ЗАКРЫТО НАРУЖУ НАМЕРЕННО: снаружи у каждого события прогресса свой
        // глагол (выбрал / переиграть / началась / дочитана / загрузил сейв).
        // Общий SetCurrent не говорит, ЧТО произошло, — а от этого зависит,
        // снимать ли ожидающий перезапуск и считать ли главу достигнутой.
        private static void SetCurrent(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(CurKey(title.id), chapter.id);
                // The NUMBER rides along: a re-import that renames chapter ids must
                // not orphan the marker — position is the player's, ids are ours.
                LvnKeep.Put(CurNumKey(title.id), chapter.number);
                if (chapter.number > Reached(title))
                    LvnKeep.Put(ReachedKey(title.id), chapter.number);
            }
        }

        /// <summary>
        /// ИГРОК ВЫБРАЛ ГЛАВУ В СПИСКЕ — «продолжить» ведёт туда.
        ///
        /// <para>Два шага, и второй легко забыть: точка продолжения переезжает
        /// И отменяется ожидающий перезапуск, если он был. Оба решения — про
        /// прогресс, но принимали их ЭКРАНЫ: карусель делала эту пару своими
        /// руками, а карточка новеллы — свою, другую. Правило «сознательный
        /// выбор главы сильнее отложенного перезапуска» жило в обработчике
        /// кнопки.</para>
        /// </summary>
        public static void ChooseChapter(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            SetCurrent(title, chapter);
            ClearRestart(title.id);
        }

        /// <summary>
        /// ИГРОК ПРОСИТ ПЕРЕИГРАТЬ ГЛАВУ С НАЧАЛА: точка продолжения переезжает
        /// на неё И ставится флаг перезапуска — цикл игры увидит его и сядет на
        /// входной чекпойнт вместо середины.
        ///
        /// <para>Автосейв здесь НЕ трогается намеренно: его сбрасывает сам цикл,
        /// когда перезапуск действительно начался. Иначе неудачная загрузка
        /// главы (нет сети, пропал скрипт) уничтожила бы позицию, которую игрок
        /// ещё держит.</para>
        /// </summary>
        public static void RestartChapter(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            SetCurrent(title, chapter);
            RequestRestart(title.id, chapter.id);
        }

        /// <summary>ГЛАВА НАЧАЛАСЬ: «продолжить» ведёт в неё, и она же считается
        /// достигнутой. Отдельное имя рядом с «выбрал» и «переиграть» —
        /// чтобы у каждого события прогресса был свой глагол, а не общий
        /// SetCurrent, по которому не видно, что именно произошло.</summary>
        public static void StartChapter(LvnTitle title, LvnChapter chapter)
            => SetCurrent(title, chapter);

        /// <summary>ЗАГРУЖЕНО СОХРАНЕНИЕ ИЗ ДРУГОЙ ГЛАВЫ: «продолжить» едет за
        /// игроком туда, куда он прыгнул. Пятый повод сдвинуть точку — и
        /// единственный, который до сих пор двигал её мимо глаголов, прямым
        /// вызовом из загрузчика сохранений.</summary>
        public static void ResumeFromSave(LvnTitle title, LvnChapter chapter)
            => SetCurrent(title, chapter);

        /// <summary>
        /// ГЛАВА ДОЧИТАНА ДО КОНЦА. Прогресс двигает ИМЕННО ФИНАЛ, а не тап
        /// «Дальше»: выход через меню конца главы раньше оставлял точку на
        /// уже пройденной главе, и «Играть» переигрывал её сначала.
        ///
        /// <para>Есть следующая — точка переезжает на неё; нет — новелла
        /// пройдена, точка снимается совсем, и повтор начнётся с начала.</para>
        /// </summary>
        public static void FinishChapter(LvnTitle title, LvnChapter next)
        {
            if (title == null) return;
            if (next != null) SetCurrent(title, next);
            else ClearCurrent(title);
        }

        /// <summary>The chapter to continue from, or null to start fresh.</summary>
        public static LvnChapter Current(LvnTitle title)
        {
            if (title?.seasons == null) return null;
            var id = LvnKeep.Get(CurKey(title.id), "");
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in title.seasons)
                if (s?.chapters != null)
                    foreach (var c in s.chapters)
                        if (c != null && c.id == id)
                            return c;
            // The id vanished (a re-import renamed chapters) — recover by the
            // stored NUMBER and heal the marker. Losing a whole playthrough to
            // an id rename is exactly the progress loss this store must forbid.
            int num = LvnKeep.Get(CurNumKey(title.id), 0);
            if (num > 0)
                foreach (var s in title.seasons)
                    if (s?.chapters != null)
                        foreach (var c in s.chapters)
                            if (c != null && c.number == num)
                            {
                                SetCurrent(title, c);
                                return c;
                            }
            return null; // nothing to recover — genuinely fresh
        }

        /// <summary>The furthest chapter number ever started (0 = nothing yet).</summary>
        public static int Reached(LvnTitle title) =>
            title == null ? 0 : LvnKeep.Get(ReachedKey(title.id), 0);

        /// <summary>
        /// НОВЕЛЛА ПРОЙДЕНА? Дошли до её последней главы, и она закончилась:
        /// продолжения нет, а самая дальняя достигнутая — последняя.
        ///
        /// <para>Правило стояло в двух местах, и защиту имело только одно.
        /// «Не начата — значит не пройдена» пришлось дописать, когда новелла с
        /// первой главой под номером 0 объявлялась пройденной на ЧИСТОМ
        /// устройстве: «дошёл до 0» ≥ «последняя 0». Воронка тогда не включалась
        /// ни разу, и игрок сразу видел витрину. Второе место — список глав в
        /// карточке — этой защиты не получило и рисовало главы непочатой
        /// новеллы галочками «пройдено».</para>
        ///
        /// <para>Урок ровно тот же, что у имени актёра и у доступности главы:
        /// знание было В ДВИЖКЕ и не дошло до соседа, потому что правило
        /// пересказали, а не позвали.</para>
        /// </summary>
        public static bool Finished(LvnTitle title)
        {
            if (title == null) return false;
            var chapters = title.ChaptersOf();
            if (chapters.Count == 0) return false;
            int reached = Reached(title);
            if (reached <= 0) return false;
            return Current(title) == null && reached >= chapters[chapters.Count - 1].number;
        }

        /// <summary>Forget the continue point (the novel was finished — replays
        /// start clean). Reached is kept so the picker stays unlocked.</summary>
        public static void ClearCurrent(LvnTitle title)
        {
            if (title == null) return;
            // Стирание фиксируется наравне с записью: без этого пройденная
            // новелла после краха снова открывалась «в середине».
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(CurKey(title.id));
                LvnKeep.Drop(CurNumKey(title.id));
            }
        }

        /// <summary>Vault restore: re-plant a marker recovered from the progress
        /// backup (id resolved against the live manifest by the caller).</summary>
        public static void RestoreMarker(string titleId, string chapterId, int number, int reached)
        {
            if (string.IsNullOrEmpty(titleId)) return;
            if (!string.IsNullOrEmpty(chapterId))
            {
                LvnKeep.Put(CurKey(titleId), chapterId);
                LvnKeep.Put(CurNumKey(titleId), number);
            }
            if (reached > LvnKeep.Get(ReachedKey(titleId), 0))
                LvnKeep.Put(ReachedKey(titleId), reached);
        }

        // ── chapter-entry checkpoints ────────────────────────────────────────
        // The genre-standard restart semantics: "start from chapter N" resets the
        // variables to what they were when chapter N was FIRST entered on this
        // playthrough — not to whatever the player has accumulated since (stats
        // from the future would leak into the past and mis-gate choices). The
        // play loop snapshots the seed vars on every fresh chapter entry; the
        // picker requests a restart, and the loop seeds from the checkpoint.

        private static string EntryKey(string titleId) => "lvn_entry_" + (titleId ?? "");
        private static string RestartKey(string titleId) => "lvn_restart_" + (titleId ?? "");

        /// <summary>Snapshot the variables as they were entering a chapter.</summary>
        public static void SaveCheckpoint(string titleId, string chapterId, JObject vars)
        {
            if (string.IsNullOrEmpty(chapterId)) return;
            try
            {
                var all = ReadCheckpoints(titleId);
                all[chapterId] = vars ?? new JObject();
                LvnKeep.Put(EntryKey(titleId), all.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { /* checkpoints are a comfort feature — never fatal */ }
        }

        /// <summary>The variables as of the chapter's first entry, or null when
        /// it was never entered (→ seed empty on a picked restart).</summary>
        public static JObject Checkpoint(string titleId, string chapterId)
        {
            if (string.IsNullOrEmpty(chapterId)) return null;
            return ReadCheckpoints(titleId)[chapterId] as JObject;
        }

        private static JObject ReadCheckpoints(string titleId)
        {
            try
            {
                var s = LvnKeep.Get(EntryKey(titleId), "");
                return string.IsNullOrEmpty(s) ? new JObject() : JObject.Parse(s);
            }
            catch { return new JObject(); }
        }

        /// <summary>The picker calls this: "the next entry into this chapter is an
        /// explicit RESTART — seed from its checkpoint, not the live state".</summary>
        public static void RequestRestart(string titleId, string chapterId)
        {
            LvnKeep.Put(RestartKey(titleId), chapterId ?? "");
        }

        /// <summary>Peek the pending restart target without consuming it — the
        /// shell checks whether the incoming play is an explicit from-the-top
        /// restart (which re-asks the player's name like any fresh start).</summary>
        public static string PendingRestart(string titleId) =>
            LvnKeep.Get(RestartKey(titleId), "");

        /// <summary>Withdraw a pending restart request — the player chose to
        /// continue their held position instead.</summary>
        public static void ClearRestart(string titleId) => LvnKeep.Drop(RestartKey(titleId));

        /// <summary>Consume the pending restart request (one-shot): whatever
        /// chapter enters FIRST after the pick clears the flag — a stale request
        /// left by a failed load must never fire on a later, unrelated chapter
        /// transition. Returns true only when the entering chapter IS the one
        /// that was picked.</summary>
        public static bool TakeRestart(string titleId, string chapterId)
        {
            var pending = LvnKeep.Get(RestartKey(titleId), "");
            if (string.IsNullOrEmpty(pending)) return false;
            // Гашение фиксируется: незафиксированное стирание воскрешало
            // залежавшийся запрос после краха — ровно то, чего требует
            // избежать комментарий выше.
            LvnKeep.Drop(RestartKey(titleId));
            return pending == chapterId;
        }

        /// <summary>A full "restart the whole expedition" wipe: forget the continue
        /// point, the furthest-reached marker, every entry checkpoint and any pending
        /// restart request. The next play starts the title from chapter one, clean.
        /// (Persisted stats and save slots live elsewhere — the host clears those.)</summary>
        public static void ResetTitle(string titleId)
        {
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(CurKey(titleId));
                LvnKeep.Drop(CurNumKey(titleId));
                LvnKeep.Drop(ReachedKey(titleId));
                LvnKeep.Drop(EntryKey(titleId));
                LvnKeep.Drop(RestartKey(titleId));
            }
        }
    }
}
