using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// The host contract — the seam that makes the engine a construction kit.
    /// The engine walks the .lvn and drives flow control; rendering is yours.
    /// Implement this on whatever presents your game (UI Toolkit, uGUI, a
    /// console test host) and the same engine plays any story.
    /// </summary>
    public interface ILvnStage
    {
        /// <summary>
        /// A line to display. <paramref name="who"/> is null for narration.
        /// The engine pauses here until the host calls <see cref="LvnPlayer.Advance"/>.
        /// </summary>
        void ShowSay(string who, string text, string style);

        /// <summary>
        /// A choice point. Present the options; when the player picks one, call
        /// <see cref="LvnPlayer.Choose"/> with its <see cref="LvnOption.Index"/>.
        /// </summary>
        void ShowChoice(IReadOnlyList<LvnOption> options);

        /// <summary>
        /// A non-blocking stage command (bg, actor, fade, dim, camera,
        /// particles, audio, wait, hint, preload). Read fields off the raw
        /// command; unknown-but-registered ops are the host's to interpret.
        /// </summary>
        void ApplyStage(JObject command);

        /// <summary>The script reached its end (<c>__end</c> or ran off the tail).</summary>
        void OnEnd();
    }
}
