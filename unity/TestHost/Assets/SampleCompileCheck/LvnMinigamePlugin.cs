using System.Threading.Tasks;
using UnityEngine;
using Lvn;
using Lvn.UI;

namespace Lvn.Samples
{
    /// <summary>
    /// The extension-plugin template: everything a plugin does, in one file.
    ///
    ///   1. <b>Custom script ops</b> — `LvnOps.Register` turns `ext minigame …`
    ///      lines into YOUR C# (with `Hold`/`Resume` flow control and the same
    ///      variable store `set`/`if` read).
    ///   2. <b>A quick-menu item</b> — `StageMenu.AddMenuItem`.
    ///   3. <b>A toolchain declaration</b> — the `ext-grammar.json` next to this
    ///      file teaches the validator and the IDE these ops (fields, enums,
    ///      required, hover docs), so scripts using them still pass the
    ///      zero-warnings gate.
    ///
    /// Registration runs once before the first scene loads — no scene wiring,
    /// works with any host (VnStage, NovelApp or your own ILvnStage).
    /// To ship this as a standalone UPM package, see the README next door.
    /// </summary>
    public static class LvnMinigamePlugin
    {
        static int _played;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            // `ext minigame id="river" difficulty=hard` — pauses the story,
            // "plays" a mini-game, writes the outcome into story variables and
            // resumes (optionally jumping to a lose branch).
            LvnOps.Register("minigame", (cmd, ctx) =>
            {
                ctx.Hold(); // the script waits at this line until Resume()
                RunMinigameAsync((string)cmd["id"], (string)cmd["difficulty"], won =>
                {
                    _played++;
                    ctx.Vars["minigame_won"] = won;   // readable by `if expr="minigame_won"`
                    var loseLabel = (string)cmd["on_lose"];
                    if (!won && !string.IsNullOrEmpty(loseLabel)) ctx.GoTo(loseLabel);
                    ctx.Resume();
                });
            });

            // `ext vibrate ms=80` — fire-and-forget (no Hold): the script rolls on.
            LvnOps.Register("vibrate", (cmd, ctx) =>
            {
#if UNITY_ANDROID || UNITY_IOS
                Handheld.Vibrate();
#else
                Debug.Log("[minigame-plugin] vibrate (no haptics on this platform)");
#endif
            });

            StageMenu.AddMenuItem("Мини-игры: статистика",
                stage => Debug.Log("[minigame-plugin] played this session: " + _played));
        }

        // Stand-in for a real mini-game scene: succeeds after a short beat,
        // with the odds set by difficulty. Replace with your own UI/loop —
        // the contract is only "call done(won) exactly once".
        static async void RunMinigameAsync(string id, string difficulty, System.Action<bool> done)
        {
            await Task.Delay(600);
            float odds = difficulty == "hard" ? 0.4f : difficulty == "easy" ? 0.9f : 0.65f;
            bool won = Random.value < odds;
            Debug.Log("[minigame-plugin] '" + id + "' (" + (difficulty ?? "normal") + ") → " + (won ? "won" : "lost"));
            done(won);
        }
    }
}
