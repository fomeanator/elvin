using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// The resume-truthfulness guard: at EVERY pause of a playthrough, a
    /// snapshot restored into a fresh player and replayed must rebuild exactly
    /// the scene the live run shows — same backdrop, same visible actors, same
    /// final axis values. This property is what protects saves across every
    /// future refactor; the cases below each encode a shipped bug:
    ///   - ops from never-taken branches leaking into the rebuilt scene,
    ///   - a hidden-then-reshown actor lost because the hide's show:false
    ///     survived a merge over a show-op that omitted the field,
    ///   - axis values (emotion) sticking from PAST ops the live path dropped.
    /// </summary>
    public class ReplayTruthTests
    {
        // A stage that models exactly what replay must reproduce: the latest
        // backdrop and each actor's latest full command (visibility + fields).
        private sealed class SceneModel : ILvnStage
        {
            public string Bg;
            public readonly Dictionary<string, JObject> Actors = new Dictionary<string, JObject>();

            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
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
        }

        private static void AssertSceneEquals(SceneModel live, SceneModel replayed, string when)
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

        // Play the doc live, choosing by the given picks; after EVERY pause,
        // snapshot → fresh player → restore + ReplayVisuals → compare scenes.
        private static void RunTruthCheck(string json, params int[] picks)
        {
            var live = new SceneModel();
            var player = new LvnPlayer(LvnDocument.Parse(json), live);
            int pick = 0;
            int guard = 0;
            player.Advance();
            while (!player.Finished && guard++ < 200)
            {
                var snap = player.Save();
                var replayed = new SceneModel();
                var resumed = new LvnPlayer(LvnDocument.Parse(json), replayed);
                resumed.Restore(snap);
                resumed.ReplayVisuals(resumed.Index);
                AssertSceneEquals(live, replayed, "@index " + snap.Index);

                if (player.AtChoice)
                    player.Choose(pick < picks.Length ? picks[pick++] : 0);
                else
                    player.Advance();
            }
            Assert.Less(guard, 200, "runaway script");
        }

        [Test]
        public void Replay_IgnoresOpsFromBranchesNeverTaken()
        {
            // the false-branch bg/actor sit LATER in the file — a linear scan
            // would let them win; the truthful path must not
            var json = @"{""script"":[
                {""op"":""set"",""key"":""flag"",""value"":true},
                {""op"":""bg"",""sprite_url"":""beach.png""},
                {""op"":""if"",""expr"":""flag"",""then"":""taken""},
                {""op"":""bg"",""sprite_url"":""castle.png""},
                {""op"":""actor"",""id"":""ghost"",""position"":""left""},
                {""op"":""say"",""text"":""never here""},
                {""op"":""label"",""id"":""taken""},
                {""op"":""say"",""text"":""on the beach""},
                {""op"":""say"",""text"":""still there""}
            ]}";
            RunTruthCheck(json);
        }

        [Test]
        public void Replay_KeepsAnActorReshownWithoutAnExplicitShow()
        {
            // show → hide → re-issue WITHOUT `show` (live rule: re-issue shows);
            // the old merge kept show:false and dropped her from the rebuild
            var json = @"{""script"":[
                {""op"":""actor"",""id"":""ash"",""position"":""left"",""emotion"":""happy""},
                {""op"":""say"",""text"":""hello""},
                {""op"":""actor"",""id"":""ash"",""show"":false},
                {""op"":""say"",""text"":""gone""},
                {""op"":""actor"",""id"":""ash"",""position"":""right""},
                {""op"":""say"",""text"":""back""},
                {""op"":""say"",""text"":""tail""}
            ]}";
            RunTruthCheck(json);
        }

        [Test]
        public void Replay_EndsHiddenWhenTheLastWordWasAHide()
        {
            var json = @"{""script"":[
                {""op"":""actor"",""id"":""ash"",""position"":""left""},
                {""op"":""say"",""text"":""hello""},
                {""op"":""actor"",""id"":""ash"",""show"":false},
                {""op"":""say"",""text"":""alone now""},
                {""op"":""say"",""text"":""tail""}
            ]}";
            RunTruthCheck(json);
        }

        [Test]
        public void Replay_TakesAxisValuesFromTheLastOpOnly()
        {
            // angry earlier, neutral (no emotion field) later — the live path
            // resolves NO emotion on the last op; a replay must not resurrect
            // the anger from the merged past
            var json = @"{""script"":[
                {""op"":""actor"",""id"":""ash"",""position"":""left"",""emotion"":""angry""},
                {""op"":""say"",""text"":""grr""},
                {""op"":""actor"",""id"":""ash"",""position"":""left""},
                {""op"":""say"",""text"":""calm""},
                {""op"":""say"",""text"":""tail""}
            ]}";
            RunTruthCheck(json);
        }

        [Test]
        public void Replay_SurvivesAChoiceDrivenBranch()
        {
            var json = @"{""script"":[
                {""op"":""bg"",""sprite_url"":""hall.png""},
                {""op"":""say"",""text"":""pick""},
                {""op"":""choice"",""options"":[
                    {""text"":""left"",""goto"":""L""},
                    {""text"":""right"",""goto"":""R""}
                ]},
                {""op"":""label"",""id"":""L""},
                {""op"":""bg"",""sprite_url"":""left.png""},
                {""op"":""actor"",""id"":""ash"",""position"":""left""},
                {""op"":""say"",""text"":""went left""},
                {""op"":""goto"",""label"":""end""},
                {""op"":""label"",""id"":""R""},
                {""op"":""bg"",""sprite_url"":""right.png""},
                {""op"":""actor"",""id"":""bones"",""position"":""right""},
                {""op"":""say"",""text"":""went right""},
                {""op"":""label"",""id"":""end""},
                {""op"":""say"",""text"":""tail""}
            ]}";
            RunTruthCheck(json, 0);
            RunTruthCheck(json, 1);
        }
    }
}
