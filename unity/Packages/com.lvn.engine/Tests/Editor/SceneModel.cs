using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// The reference scene a player "sees": the latest backdrop plus each
    /// actor's latest full command (visibility + fields), mirroring the live
    /// sticky rules. Shared by the resume-truth fixtures and the soak bot so
    /// test land has ONE definition of scene equality.
    /// </summary>
    internal sealed class SceneModel : ILvnStage
    {
        public string Bg;
        public readonly Dictionary<string, JObject> Actors = new Dictionary<string, JObject>();

        /// <summary>The options of the most recent choice pause — what a bot
        /// picks from (SceneModel itself renders nothing).</summary>
        public IReadOnlyList<LvnOption> LastOptions;

        public void ShowSay(string who, string text, string style) { }
        public void ShowChoice(IReadOnlyList<LvnOption> options) => LastOptions = options;
        public void OnEnd() { }

        public void ApplyStage(JObject c)
        {
            switch ((string)c["op"])
            {
                case "bg":
                    Bg = (string)c["sprite_url"];
                    break;
                case "actor":
                    var id = (string)c["id"];
                    if (string.IsNullOrEmpty(id)) return;
                    if (!Actors.TryGetValue(id, out var st)) { st = new JObject(); Actors[id] = st; }
                    // mirror the live sticky rule: placement fields persist,
                    // everything else is the current command's word
                    var sticky = new JObject();
                    foreach (var keep in new[] { "position", "x", "y" })
                        if (st[keep] != null) sticky[keep] = st[keep];
                    st.RemoveAll();
                    foreach (var p in sticky.Properties()) st[p.Name] = p.Value;
                    foreach (var p in c.Properties())
                        if (p.Name != "op") st[p.Name] = p.Value.DeepClone();
                    st["__visible"] = c["show"] == null || (bool?)c["show"] != false;
                    break;
            }
        }

        public HashSet<string> Visible()
        {
            var v = new HashSet<string>();
            foreach (var kv in Actors)
                if ((bool?)kv.Value["__visible"] == true) v.Add(kv.Key);
            return v;
        }

        public static void AssertSameScene(SceneModel live, SceneModel replayed, string when)
        {
            Assert.AreEqual(live.Bg, replayed.Bg, when + ": backdrop diverged");
            var lv = live.Visible();
            var rv = replayed.Visible();
            Assert.IsTrue(lv.SetEquals(rv),
                when + $": visible actors diverged (live [{string.Join(",", lv)}] vs replay [{string.Join(",", rv)}])");
            foreach (var id in lv)
            {
                // the fields the player SEES: emotion/outfit resolve from the
                // final command — a replay must land on the same values
                foreach (var field in new[] { "emotion", "outfit", "position" })
                {
                    var a = (string)live.Actors[id][field];
                    var b = (string)replayed.Actors[id][field];
                    Assert.AreEqual(a, b, when + $": {id}.{field} diverged");
                }
            }
        }
    }
}
