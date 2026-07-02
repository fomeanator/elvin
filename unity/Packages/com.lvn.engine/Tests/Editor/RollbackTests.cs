using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    // Rollback: a bounded history of beats (says / standalone choices), each
    // snapshotted BEFORE it runs — so stepping back undoes a mis-tapped choice's
    // set/inc and re-presents the options.
    public class RollbackTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public string Last => Lines.Count > 0 ? Lines[Lines.Count - 1] : null;
            public IReadOnlyList<LvnOption> Options;
            public int ChoiceShown;
            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { Options = options; ChoiceShown++; }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static (LvnPlayer p, RecStage s) Make(string json)
        {
            var s = new RecStage();
            return (new LvnPlayer(LvnDocument.Parse(json), s), s);
        }

        [Test]
        public void RollbackReturnsToPreviousLine()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""say"",""text"":""один""},
                {""op"":""say"",""text"":""два""},
                {""op"":""say"",""text"":""три""}
            ]}");
            p.Advance(); // "один"
            p.Advance(); // "два"
            Assert.AreEqual("два", s.Last);
            Assert.IsTrue(p.CanRollback);

            var snap = p.PopRollback();
            Assert.IsNotNull(snap);
            p.Restore(snap);
            p.ContinueFrom(p.Index);
            Assert.AreEqual("один", s.Last, "rollback re-shows the previous line");

            p.Advance();
            Assert.AreEqual("два", s.Last, "play continues forward normally after a rollback");
        }

        [Test]
        public void RollbackUndoesAChoicesVariables()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""say"",""text"":""старт""},
                {""op"":""label"",""id"":""ask""},
                {""op"":""choice"",""options"":[
                    {""text"":""взять"",""body"":[
                        {""op"":""set"",""key"":""gold"",""value"":10},
                        {""op"":""goto"",""label"":""after""}
                    ]},
                    {""text"":""мимо"",""goto"":""after""}
                ]},
                {""op"":""label"",""id"":""after""},
                {""op"":""say"",""text"":""дальше""}
            ]}");
            p.Advance(); // "старт"
            p.Advance(); // standalone choice shown
            Assert.IsTrue(p.AtChoice);
            p.Choose(0); // sets gold=10
            p.Advance(); // "дальше"
            Assert.AreEqual(10d, (double)p.Vars["gold"], 0.001);

            // Roll back from "дальше" → the choice beat, with gold UNSET again.
            var snap = p.PopRollback();
            Assert.IsNotNull(snap);
            p.Restore(snap);
            p.ContinueFrom(p.Index);
            Assert.IsTrue(p.AtChoice, "the choice is re-presented");
            Assert.IsFalse(p.Vars.ContainsKey("gold"), "the picked option's set is undone");
        }

        [Test]
        public void CannotRollbackAtFirstBeat()
        {
            var (p, _) = Make(@"{""script"":[{""op"":""say"",""text"":""x""},{""op"":""say"",""text"":""y""}]}");
            p.Advance();
            Assert.IsFalse(p.CanRollback);
            Assert.IsNull(p.PopRollback());
        }

        [Test]
        public void HistoryIsCapped()
        {
            var cmds = new List<string>();
            for (int i = 0; i < LvnPlayer.MaxHistory + 30; i++)
                cmds.Add($@"{{""op"":""say"",""text"":""line {i}""}}");
            var (p, _) = Make(@"{""script"":[" + string.Join(",", cmds) + "]}");

            for (int i = 0; i < LvnPlayer.MaxHistory + 30; i++) p.Advance();

            // Walk all the way back — must terminate well within the cap.
            int steps = 0;
            while (p.CanRollback && steps < LvnPlayer.MaxHistory + 50)
            {
                var snap = p.PopRollback();
                Assert.IsNotNull(snap);
                p.Restore(snap);
                p.ContinueFrom(p.Index);
                steps++;
            }
            Assert.LessOrEqual(steps, LvnPlayer.MaxHistory, "history depth is bounded");
            Assert.Greater(steps, 50, "a meaningful trail is kept");
        }

        [Test]
        public void SayFollowedByChoiceIsOneBeat()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""say"",""text"":""интро""},
                {""op"":""say"",""text"":""вопрос?""},
                {""op"":""choice"",""options"":[{""text"":""да"",""goto"":""after""}]},
                {""op"":""label"",""id"":""after""},
                {""op"":""say"",""text"":""финал""}
            ]}");
            p.Advance(); // "интро"
            p.Advance(); // "вопрос?" + its paired choice in one step
            Assert.IsTrue(p.AtChoice);
            p.Choose(0);
            p.Advance(); // "финал"

            var snap = p.PopRollback();
            p.Restore(snap);
            p.ContinueFrom(p.Index);
            Assert.AreEqual("вопрос?", s.Last, "rollback lands on the say, so the line shows WITH its options");
            Assert.IsTrue(p.AtChoice);
        }

        [Test]
        public void RepresentedChoiceDoesNotGrowHistory()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""ask""},
                {""op"":""choice"",""options"":[{""text"":""ok"",""goto"":""ask2""}]},
                {""op"":""label"",""id"":""ask2""},
                {""op"":""say"",""text"":""b""}
            ]}");
            p.Advance(); // "a"
            p.Advance(); // choice
            int shown = s.ChoiceShown;
            p.Advance(); // a stray tap while the choice is up re-presents it
            Assert.Greater(s.ChoiceShown, shown);

            // History still holds exactly [say a, choice]: one rollback returns "a".
            var snap = p.PopRollback();
            Assert.IsNotNull(snap);
            p.Restore(snap);
            p.ContinueFrom(p.Index);
            Assert.AreEqual("a", s.Last);
            Assert.IsFalse(p.CanRollback, "no duplicate beats were recorded");
        }

        [Test]
        public void ClearHistoryDropsTheTrail()
        {
            var (p, _) = Make(@"{""script"":[
                {""op"":""say"",""text"":""x""},{""op"":""say"",""text"":""y""},{""op"":""say"",""text"":""z""}
            ]}");
            p.Advance(); p.Advance(); p.Advance();
            Assert.IsTrue(p.CanRollback);
            p.ClearHistory();
            Assert.IsFalse(p.CanRollback);
        }
    }
}
