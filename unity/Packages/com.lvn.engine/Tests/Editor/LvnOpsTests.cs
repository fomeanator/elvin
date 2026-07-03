using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// The host's escape hatch: custom script ops registered from C#. Flow
    /// control (Hold/Resume/GoTo), variable access, and the guarantee that
    /// unregistered unknown ops keep the engine's ignore behaviour.
    public class LvnOpsTests
    {
        private sealed class FakeStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        [TearDown]
        public void Clean() => LvnOps.Clear();

        private static LvnPlayer Play(string json, out FakeStage stage)
        {
            stage = new FakeStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        private const string Script = @"{""script"":[
            {""op"":""say"",""text"":""before""},
            {""op"":""minigame"",""kind"":""lockpick""},
            {""op"":""say"",""text"":""after""}
        ]}";

        [Test]
        public void CustomOp_RunsWithVarsAndContinues()
        {
            string seenKind = null;
            LvnOps.Register("minigame", (cmd, ctx) =>
            {
                seenKind = (string)cmd["kind"];
                ctx.Vars["attempts"] = 3;
            });
            var p = Play(Script, out var stage);
            p.Advance(); // before
            p.Advance(); // custom op (fire-and-forget) + after
            Assert.AreEqual("lockpick", seenKind);
            Assert.AreEqual(3, (int)p.Vars["attempts"]);
            Assert.AreEqual("after", stage.Lines[stage.Lines.Count - 1], "script rolled on past the op");
        }

        [Test]
        public void Hold_PausesUntilResume()
        {
            ILvnOpContext held = null;
            LvnOps.Register("minigame", (cmd, ctx) => { ctx.Hold(); held = ctx; });
            var p = Play(Script, out var stage);
            p.Advance(); // before
            p.Advance(); // hits the op → holds
            Assert.AreEqual(1, stage.Lines.Count, "script paused at the op");
            Assert.IsNotNull(held);

            held.Resume(); // the minigame finished (a later frame in real life)
            Assert.AreEqual("after", stage.Lines[stage.Lines.Count - 1], "Resume continues the story");
        }

        [Test]
        public void GoTo_ReroutesTheStory()
        {
            const string json = @"{""script"":[
                {""op"":""branch""},
                {""op"":""say"",""text"":""skipped""},
                {""op"":""label"",""id"":""won""},
                {""op"":""say"",""text"":""victory""}
            ]}";
            LvnOps.Register("branch", (cmd, ctx) => ctx.GoTo("won"));
            var p = Play(json, out var stage);
            p.Advance();
            Assert.AreEqual("victory", stage.Lines[0], "handler's GoTo won over fall-through");
        }

        [Test]
        public void UnregisteredUnknownOp_IsStillIgnored()
        {
            var p = Play(Script, out var stage); // nothing registered
            p.Advance();
            p.Advance();
            Assert.AreEqual(2, stage.Lines.Count, "unknown op flows through untouched");
            Assert.AreEqual("after", stage.Lines[1]);
        }
    }
}
