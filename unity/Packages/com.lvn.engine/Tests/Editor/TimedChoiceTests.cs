using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Timed choices at the player level: the timeout metadata the stage reads,
    /// and the expiry jump — plus the guarantees that untimed choices and stale
    /// timers are no-ops.
    public class TimedChoiceTests
    {
        private sealed class FakeStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public IReadOnlyList<LvnOption> Shown;
            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Shown = options;
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private const string Timed = @"{""script"":[
            {""op"":""choice"",""timeout"":7,""timeout_goto"":""late"",""options"":[
                {""text"":""Да"",""goto"":""yes""}]},
            {""op"":""label"",""id"":""yes""},
            {""op"":""say"",""text"":""успел""},
            {""op"":""goto"",""label"":""__end""},
            {""op"":""label"",""id"":""late""},
            {""op"":""say"",""text"":""время вышло""}
        ]}";

        [Test]
        public void TimeoutMetadata_VisibleWhileAtChoice()
        {
            var stage = new FakeStage();
            var p = new LvnPlayer(LvnDocument.Parse(Timed), stage);
            p.Advance();
            Assert.IsTrue(p.AtChoice);
            Assert.AreEqual(7f, p.CurrentChoiceTimeout, 0.001f);
        }

        [Test]
        public void Expiry_TakesTheTimeoutBranch()
        {
            var stage = new FakeStage();
            var p = new LvnPlayer(LvnDocument.Parse(Timed), stage);
            p.Advance();
            Assert.IsTrue(p.ResolveChoiceTimeout());
            Assert.AreEqual("время вышло", stage.Lines[stage.Lines.Count - 1]);
        }

        [Test]
        public void PickBeforeExpiry_WinsNormally()
        {
            var stage = new FakeStage();
            var p = new LvnPlayer(LvnDocument.Parse(Timed), stage);
            p.Advance();
            p.Choose(stage.Shown[0].Index);
            p.Advance();
            Assert.AreEqual("успел", stage.Lines[stage.Lines.Count - 1]);
            Assert.IsFalse(p.ResolveChoiceTimeout(), "a stale timer after the pick is a no-op");
        }

        [Test]
        public void UntimedChoice_ReportsZeroAndIgnoresExpiry()
        {
            const string plain = @"{""script"":[
                {""op"":""choice"",""options"":[{""text"":""a"",""goto"":""x""}]},
                {""op"":""label"",""id"":""x""}
            ]}";
            var stage = new FakeStage();
            var p = new LvnPlayer(LvnDocument.Parse(plain), stage);
            p.Advance();
            Assert.AreEqual(0f, p.CurrentChoiceTimeout);
            Assert.IsFalse(p.ResolveChoiceTimeout());
            Assert.IsTrue(p.AtChoice, "the untimed choice still waits for the player");
        }
    }
}
