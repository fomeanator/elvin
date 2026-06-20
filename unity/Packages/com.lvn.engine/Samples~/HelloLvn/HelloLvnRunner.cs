using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Samples
{
    /// <summary>
    /// The smallest possible LVN host: it logs the story to the Console and
    /// advances on click. It shows the whole contract — load a .lvn, build an
    /// <see cref="LvnPlayer"/>, implement <see cref="ILvnStage"/>, alternate
    /// Advance/Choose. Replace the Debug.Log calls with your real UI and you
    /// have a game.
    ///
    /// Setup: drop this on a GameObject, assign the bundled <c>hello.lvn.txt</c>
    /// to <see cref="script"/>, press Play, and click to read on.
    /// </summary>
    public sealed class HelloLvnRunner : MonoBehaviour, ILvnStage
    {
        [Tooltip("A .lvn file imported as a TextAsset (e.g. hello.lvn.txt).")]
        public TextAsset script;

        private LvnPlayer _player;
        private bool _awaitingTap;
        private IReadOnlyList<LvnOption> _options;

        private void Start()
        {
            if (script == null)
            {
                Debug.LogError("HelloLvnRunner: assign a .lvn TextAsset to 'script'.");
                enabled = false;
                return;
            }
            var doc = LvnDocument.Parse(script.text);
            _player = new LvnPlayer(doc, this);
            _player.Advance();
        }

        private void Update()
        {
            if (_player == null || _player.Finished) return;

            if (_options != null)
            {
                // Pick an option with number keys 1..9.
                for (int i = 0; i < _options.Count && i < 9; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    {
                        int chosen = _options[i].Index;
                        _options = null;
                        _player.Choose(chosen);
                        _player.Advance();
                        return;
                    }
                }
                return;
            }

            if (_awaitingTap && Input.GetMouseButtonDown(0))
            {
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // ── ILvnStage ─────────────────────────────────────────────────────────

        public void ShowSay(string who, string text, string style)
        {
            Debug.Log(string.IsNullOrEmpty(who) ? text : $"{who}: {text}");
            _awaitingTap = true; // click to continue
        }

        public void ShowChoice(IReadOnlyList<LvnOption> options)
        {
            _options = options;
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                var cost = string.IsNullOrEmpty(o.Cost) ? "" : $"   ({o.Cost})";
                Debug.Log($"  [{i + 1}] {o.Text}{cost}");
            }
            Debug.Log("Press 1.." + options.Count + " to choose.");
        }

        public void ApplyStage(JObject command)
        {
            // A real host renders bg/actor/fade/etc. here. The sample just notes them.
            Debug.Log($"<stage: {(string)command["op"]}>");
        }

        public void OnEnd()
        {
            Debug.Log("— end —");
        }
    }
}
