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
        public static void SetCurrent(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            PlayerPrefs.SetString(CurKey(title.id), chapter.id);
            // The NUMBER rides along: a re-import that renames chapter ids must
            // not orphan the marker — position is the player's, ids are ours.
            PlayerPrefs.SetInt(CurNumKey(title.id), chapter.number);
            if (chapter.number > Reached(title))
                PlayerPrefs.SetInt(ReachedKey(title.id), chapter.number);
            PlayerPrefs.Save();
        }

        /// <summary>The chapter to continue from, or null to start fresh.</summary>
        public static LvnChapter Current(LvnTitle title)
        {
            if (title?.seasons == null) return null;
            var id = PlayerPrefs.GetString(CurKey(title.id), "");
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in title.seasons)
                if (s?.chapters != null)
                    foreach (var c in s.chapters)
                        if (c != null && c.id == id)
                            return c;
            // The id vanished (a re-import renamed chapters) — recover by the
            // stored NUMBER and heal the marker. Losing a whole playthrough to
            // an id rename is exactly the progress loss this store must forbid.
            int num = PlayerPrefs.GetInt(CurNumKey(title.id), 0);
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
            title == null ? 0 : PlayerPrefs.GetInt(ReachedKey(title.id), 0);

        /// <summary>Forget the continue point (the novel was finished — replays
        /// start clean). Reached is kept so the picker stays unlocked.</summary>
        public static void ClearCurrent(LvnTitle title)
        {
            if (title == null) return;
            PlayerPrefs.DeleteKey(CurKey(title.id));
            PlayerPrefs.DeleteKey(CurNumKey(title.id));
        }

        /// <summary>Vault restore: re-plant a marker recovered from the progress
        /// backup (id resolved against the live manifest by the caller).</summary>
        public static void RestoreMarker(string titleId, string chapterId, int number, int reached)
        {
            if (string.IsNullOrEmpty(titleId)) return;
            if (!string.IsNullOrEmpty(chapterId))
            {
                PlayerPrefs.SetString(CurKey(titleId), chapterId);
                PlayerPrefs.SetInt(CurNumKey(titleId), number);
            }
            if (reached > PlayerPrefs.GetInt(ReachedKey(titleId), 0))
                PlayerPrefs.SetInt(ReachedKey(titleId), reached);
            PlayerPrefs.Save();
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
                PlayerPrefs.SetString(EntryKey(titleId), all.ToString(Newtonsoft.Json.Formatting.None));
                PlayerPrefs.Save();
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
                var s = PlayerPrefs.GetString(EntryKey(titleId), "");
                return string.IsNullOrEmpty(s) ? new JObject() : JObject.Parse(s);
            }
            catch { return new JObject(); }
        }

        /// <summary>The picker calls this: "the next entry into this chapter is an
        /// explicit RESTART — seed from its checkpoint, not the live state".</summary>
        public static void RequestRestart(string titleId, string chapterId)
        {
            PlayerPrefs.SetString(RestartKey(titleId), chapterId ?? "");
            PlayerPrefs.Save();
        }

        /// <summary>Peek the pending restart target without consuming it — the
        /// shell checks whether the incoming play is an explicit from-the-top
        /// restart (which re-asks the player's name like any fresh start).</summary>
        public static string PendingRestart(string titleId) =>
            PlayerPrefs.GetString(RestartKey(titleId), "");

        /// <summary>Withdraw a pending restart request — the player chose to
        /// continue their held position instead.</summary>
        public static void ClearRestart(string titleId) =>
            PlayerPrefs.DeleteKey(RestartKey(titleId));

        /// <summary>Consume the pending restart request (one-shot): whatever
        /// chapter enters FIRST after the pick clears the flag — a stale request
        /// left by a failed load must never fire on a later, unrelated chapter
        /// transition. Returns true only when the entering chapter IS the one
        /// that was picked.</summary>
        public static bool TakeRestart(string titleId, string chapterId)
        {
            var pending = PlayerPrefs.GetString(RestartKey(titleId), "");
            if (string.IsNullOrEmpty(pending)) return false;
            PlayerPrefs.DeleteKey(RestartKey(titleId));
            return pending == chapterId;
        }

        /// <summary>A full "restart the whole expedition" wipe: forget the continue
        /// point, the furthest-reached marker, every entry checkpoint and any pending
        /// restart request. The next play starts the title from chapter one, clean.
        /// (Persisted stats and save slots live elsewhere — the host clears those.)</summary>
        public static void ResetTitle(string titleId)
        {
            PlayerPrefs.DeleteKey(CurKey(titleId));
            PlayerPrefs.DeleteKey(CurNumKey(titleId));
            PlayerPrefs.DeleteKey(ReachedKey(titleId));
            PlayerPrefs.DeleteKey(EntryKey(titleId));
            PlayerPrefs.DeleteKey(RestartKey(titleId));
            PlayerPrefs.Save();
        }
    }
}
