using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// The host's escape hatch: script ops the ENGINE doesn't know, handled by
    /// the embedding game's C# code. Register once at boot —
    ///
    ///   LvnOps.Register("minigame", (cmd, ctx) => {
    ///       ctx.Hold();                       // pause the script
    ///       MyMinigame.Run((string)cmd["kind"], won => {
    ///           ctx.Vars["won"] = won;
    ///           ctx.Resume();                 // …and continue when done
    ///       });
    ///   });
    ///
    /// — and every <c>minigame kind="lockpick"</c> line in any chapter routes
    /// here, with full access to the player's variables and flow control.
    /// Without a Hold() the op is fire-and-forget and the script rolls on.
    /// Unregistered unknown ops keep the engine's behaviour (ignored).
    /// </summary>
    public static class LvnOps
    {
        /// <summary>A custom op handler: the raw command + the flow context.</summary>
        public delegate void Handler(JObject cmd, ILvnOpContext ctx);

        private static readonly Dictionary<string, Handler> _handlers =
            new Dictionary<string, Handler>(StringComparer.Ordinal);

        /// <summary>Register (or replace) the handler for a custom op name.
        /// Engine-owned ops (say/choice/actor/…) cannot be overridden.</summary>
        public static void Register(string op, Handler handler)
        {
            if (string.IsNullOrEmpty(op) || handler == null) return;
            _handlers[op] = handler;
        }

        public static void Unregister(string op)
        {
            if (!string.IsNullOrEmpty(op)) _handlers.Remove(op);
        }

        /// <summary>Remove every registered handler (tests / host teardown).</summary>
        public static void Clear() => _handlers.Clear();

        internal static bool TryGet(string op, out Handler handler)
            => _handlers.TryGetValue(op ?? "", out handler);
    }

    /// <summary>What a custom op handler may touch: the story's variables and
    /// its flow. <see cref="Hold"/> pauses the script at this op (a minigame, a
    /// dialog, an await) until <see cref="Resume"/>; both are one-shot per
    /// invocation. <see cref="Stage"/> is the presentation host (the running
    /// <c>VnStage</c>) for advanced integrations.</summary>
    public interface ILvnOpContext
    {
        /// <summary>The story variables — read and write, same store `set`/`if` use.</summary>
        IDictionary<string, JToken> Vars { get; }

        /// <summary>Jump the story to a label (takes effect when the script resumes).</summary>
        void GoTo(string label);

        /// <summary>Pause the script at this op until <see cref="Resume"/>.</summary>
        void Hold();

        /// <summary>Continue a held script (safe to call from a callback later).</summary>
        void Resume();

        /// <summary>The presentation host (the engine's ILvnStage — castable to
        /// VnStage when embedding the standard stage).</summary>
        ILvnStage Stage { get; }
    }
}
