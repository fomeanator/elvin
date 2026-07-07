using System.Collections.Generic;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Pins the newly-wired text + data behaviour on the player: {var}
    /// interpolation in say lines and speaker names, computed `set expr`, and
    /// string/bool structured conditions.
    public class PlayerTextAndDataTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<(string who, string text)> Says = new List<(string, string)>();
            public void ShowSay(string who, string text, string style) => Says.Add((who, text));
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() { }
        }

        private static RecStage Run(string scriptArrayJson)
        {
            var doc = LvnDocument.Parse("{\"scene\":\"t\",\"script\":" + scriptArrayJson + "}");
            var stage = new RecStage();
            var p = new LvnPlayer(doc, stage);
            // Advance repeatedly past each say until the chapter finishes.
            for (int guard = 0; guard < 1000 && !p.Finished; guard++)
                p.Advance();
            return stage;
        }

        [Test]
        public void Say_InterpolatesVars_InTextAndSpeaker()
        {
            var s = Run(@"[
                {""op"":""set"",""key"":""hero"",""value"":""Mara""},
                {""op"":""set"",""key"":""score"",""value"":5},
                {""op"":""say"",""who"":""{hero}"",""text"":""Hi {hero}, score {score}.""}
            ]");
            Assert.AreEqual(1, s.Says.Count);
            Assert.AreEqual("Mara", s.Says[0].who);
            Assert.AreEqual("Hi Mara, score 5.", s.Says[0].text);
        }

        [Test]
        public void Set_Expr_ComputesValue()
        {
            var s = Run(@"[
                {""op"":""set"",""key"":""a"",""value"":3},
                {""op"":""set"",""key"":""b"",""expr"":""a*2 + 1""},
                {""op"":""say"",""text"":""{b}""}
            ]");
            Assert.AreEqual("7", s.Says[0].text);
        }

        [Test]
        public void GlobalStats_NestUnderOneObject_SetIncAndMemberRead()
        {
            // set/inc with a dotted key write into a nested object; if/{…} read it
            // back by member access — the cross-novel `global` stat namespace.
            var s = Run(@"[
                {""op"":""set"",""key"":""global.rep"",""value"":3},
                {""op"":""inc"",""key"":""global.rep"",""by"":2},
                {""op"":""inc"",""key"":""global.visits"",""by"":1},
                {""op"":""if"",""expr"":""global.rep >= 5"",""then"":""hi"",""else"":""lo""},
                {""op"":""label"",""id"":""hi""},{""op"":""say"",""text"":""rep {global.rep} visits {global.visits}""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""lo""},{""op"":""say"",""text"":""LO""}
            ]");
            Assert.AreEqual(1, s.Says.Count);
            Assert.AreEqual("rep 5 visits 1", s.Says[0].text);
        }

        [Test]
        public void GlobalStats_LiveUnderTheGlobalObject_ForCrossNovelPersistence()
        {
            var doc = LvnDocument.Parse("{\"scene\":\"t\",\"script\":[" +
                @"{""op"":""set"",""key"":""global.rep"",""value"":3},
                  {""op"":""inc"",""key"":""global.visits"",""by"":1},
                  {""op"":""set"",""key"":""localOnly"",""value"":9}" + "]}");
            var stage = new RecStage();
            var p = new LvnPlayer(doc, stage);
            for (int g = 0; g < 100 && !p.Finished; g++) p.Advance();

            // Every global.* stat lives inside ONE `global` object — that's the unit
            // NovelApp splits out to the shared per-player state blob.
            var global = p.Vars["global"] as Newtonsoft.Json.Linq.JObject;
            Assert.IsNotNull(global, "global stats collect under a `global` object");
            Assert.AreEqual(3, (int)global["rep"]);
            Assert.AreEqual(1, (int)global["visits"]);
            Assert.IsFalse(p.Vars.ContainsKey("global.rep"), "no flat dotted key leaks alongside");
            Assert.AreEqual(9, (int)p.Vars["localOnly"], "plain title vars are untouched");
        }

        [Test]
        public void If_StructuredCond_ComparesStringsByValue()
        {
            var s = Run(@"[
                {""op"":""set"",""key"":""name"",""value"":""Mara""},
                {""op"":""if"",""cond"":{""key"":""name"",""op"":""eq"",""value"":""Mara""},""then"":""yes"",""else"":""no""},
                {""op"":""label"",""id"":""yes""},
                {""op"":""say"",""text"":""Y""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""no""},
                {""op"":""say"",""text"":""N""}
            ]");
            Assert.AreEqual(1, s.Says.Count);
            Assert.AreEqual("Y", s.Says[0].text);
        }

        [Test]
        public void If_StructuredCond_UnsetEqualsEmptyString()
        {
            // ink semantics: an unset var equals "" on the first pass.
            var s = Run(@"[
                {""op"":""if"",""cond"":{""key"":""name"",""op"":""eq"",""value"":""""},""then"":""blank"",""else"":""named""},
                {""op"":""label"",""id"":""blank""},
                {""op"":""say"",""text"":""B""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""named""},
                {""op"":""say"",""text"":""N""}
            ]");
            Assert.AreEqual("B", s.Says[0].text);
        }
    }
}
