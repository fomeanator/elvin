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
