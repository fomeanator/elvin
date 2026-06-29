namespace Lvn
{
    /// <summary>
    /// Pure decision for WHERE a chapter resumes from. Core progress mechanics —
    /// getting it wrong either replays content the player already saw or (worse)
    /// throws away progress and the player's name. Kept Unity/disk/network-free so
    /// every case is unit-testable with synthetic inputs.
    /// </summary>
    public readonly struct ResumePlan
    {
        /// <summary>Command index to start playback from.</summary>
        public readonly int StartIndex;
        /// <summary>True if the visual stage (bg/actors) must be restored from the
        /// slot before the first command runs (i.e. resuming mid-chapter).</summary>
        public readonly bool RestoreState;
        /// <summary>True if saved player variables (flags, player name) should load.</summary>
        public readonly bool LoadVars;

        public ResumePlan(int startIndex, bool restoreState, bool loadVars)
        {
            StartIndex = startIndex;
            RestoreState = restoreState;
            LoadVars = loadVars;
        }

        public static readonly ResumePlan FromStart = new ResumePlan(0, false, false);
    }

    public static class ResumePlanner
    {
        /// <summary>
        /// Decide the resume plan for a chapter.
        /// <para>Rules (in order):</para>
        /// <list type="bullet">
        ///   <item>No slot / different script / already finished → start fresh (0).</item>
        ///   <item>Script unchanged (counts match) → resume exactly at savedIndex.</item>
        ///   <item>Script edited and the edit is BEFORE savedIndex → rewind to the
        ///   edit point (savedIndex may now point at a shifted beat).</item>
        ///   <item>Length changed but no reliable edit point → resume at savedIndex
        ///   clamped into the (possibly shorter) script. NEVER reset to 0 — that
        ///   discards progress and the player name.</item>
        /// </list>
        /// Vars load whenever a slot is being resumed; stage restore only when the
        /// resolved start index is &gt; 0.
        /// </summary>
        /// <param name="hasSlot">A save slot exists for this chapter.</param>
        /// <param name="finished">The slot is marked finished (chapter completed).</param>
        /// <param name="sameScript">The slot's script matches the current chapter.</param>
        /// <param name="savedIndex">Command index stored in the slot.</param>
        /// <param name="savedCommandCount">Command count when the slot was saved.</param>
        /// <param name="currentCommandCount">Command count of the script now.</param>
        /// <param name="lastEditAt">Lowest command index touched since the last
        /// clean publish, or null/0 if unknown.</param>
        public static ResumePlan Resolve(
            bool hasSlot, bool finished, bool sameScript,
            int savedIndex, int savedCommandCount, int currentCommandCount,
            int? lastEditAt)
        {
            var resuming = hasSlot && !finished && sameScript;
            if (!resuming) return ResumePlan.FromStart;

            int startIndex;
            if (savedCommandCount == currentCommandCount)
            {
                startIndex = savedIndex;
            }
            else if (lastEditAt.HasValue && lastEditAt.Value > 0 && savedIndex > lastEditAt.Value)
            {
                startIndex = lastEditAt.Value;
            }
            else
            {
                startIndex = Clamp(savedIndex, 0, Max(0, currentCommandCount - 1));
            }

            return new ResumePlan(startIndex, restoreState: startIndex > 0, loadVars: true);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int Max(int a, int b) => a > b ? a : b;
    }
}
