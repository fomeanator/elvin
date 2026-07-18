using System.Text;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// The HUD percent guard. Linearized imports (articy) append choice bodies
    /// at the file TAIL: picking an option teleports the cursor to ~99% of the
    /// file, plays the branch, and jumps back to the spine. The shipped bug:
    /// the monotonic high-water mark latched that tail position, so the bar
    /// showed 99% at ~14% of the story — forever. These tests pin the fixed
    /// semantics: far excursions freeze the bar, the far return resumes it,
    /// and a save restored INSIDE a body heals on the way out.
    /// </summary>
    public class ProgressBarTruthTests
    {
        private sealed class NullStage : ILvnStage
        {
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(System.Collections.Generic.IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject c) { }
            public void OnEnd() { }
        }

        // A cold-shaped script: a short spine whose choice jumps into a body
        // placed at the far tail (padded well past the FarJump window), which
        // returns to the spine. Tail padding sits after __tail so it is never
        // executed — it only stretches the file the way linearization does.
        private static string TailBodyScript(int pad = 900)
        {
            var sb = new StringBuilder();
            sb.Append(@"{""script"":[
                {""op"":""say"",""text"":""intro""},
                {""op"":""choice"",""options"":[
                    {""text"":""dive"",""goto"":""body""}
                ]},
                {""op"":""label"",""id"":""back""},
                {""op"":""say"",""text"":""spine again""},
                {""op"":""say"",""text"":""spine tail""},
                {""op"":""goto"",""label"":""__end""},");
            for (int i = 0; i < pad; i++)
                sb.Append(@"{""op"":""label"",""id"":""pad" + i + @"""},");
            sb.Append(@"
                {""op"":""label"",""id"":""body""},
                {""op"":""say"",""text"":""inside the body""},
                {""op"":""say"",""text"":""still inside""},
                {""op"":""goto"",""label"":""back""}
            ]}");
            return sb.ToString();
        }

        private static int Pct(LvnPlayer p) => Content.Percent.Value(p.ProgressIndex, p.Count);

        [Test]
        public void FarTailBody_DoesNotSpikeTheBar()
        {
            var p = new LvnPlayer(LvnDocument.Parse(TailBodyScript()), new NullStage());
            p.Advance();                       // intro (say pauses; choice shows with it)
            int atChoice = Pct(p);
            Assert.Less(atChoice, 10, "the spine start must read as early progress");

            p.Choose(0);                       // teleport into the tail body
            p.Advance();                       // "inside the body"
            Assert.AreEqual(atChoice, Pct(p), "a far excursion must FREEZE the bar, not spike it");
            p.Advance();                       // "still inside"
            Assert.AreEqual(atChoice, Pct(p), "beats inside the body still must not move the bar");

            p.Advance();                       // far return → "spine again"
            Assert.Less(Pct(p), 10, "back on the spine the bar stays honest");
            Assert.GreaterOrEqual(p.ProgressIndex, 2, "and it keeps climbing past the choice");
        }

        [Test]
        public void ResumeInsideATailBody_HealsOnTheWayOut()
        {
            var doc = LvnDocument.Parse(TailBodyScript());
            var live = new LvnPlayer(doc, new NullStage());
            live.Advance();
            live.Choose(0);
            live.Advance();                    // paused inside the body (~tail index)
            var snap = live.Save();

            var resumed = new LvnPlayer(LvnDocument.Parse(TailBodyScript()), new NullStage());
            resumed.Restore(snap);
            // the restored index IS the tail — the bar may read high here…
            resumed.ContinueFrom(resumed.Index);   // "still inside"
            resumed.Advance();                     // far return → "spine again"
            Assert.Less(Pct(resumed), 10,
                "the far return must clamp a body-latched mark back to the spine");
        }

        [Test]
        public void NearBranches_KeepTheClassicClimb()
        {
            // an inline (near) skip must keep raising the bar exactly as before
            var json = @"{""script"":[
                {""op"":""say"",""text"":""one""},
                {""op"":""set"",""key"":""flag"",""value"":true},
                {""op"":""if"",""expr"":""flag"",""then"":""skip""},
                {""op"":""say"",""text"":""never""},
                {""op"":""label"",""id"":""skip""},
                {""op"":""say"",""text"":""two""},
                {""op"":""say"",""text"":""three""}
            ]}";
            var p = new LvnPlayer(LvnDocument.Parse(json), new NullStage());
            int last = -1;
            p.Advance();
            int guard = 0;
            while (!p.Finished && guard++ < 20)
            {
                Assert.GreaterOrEqual(p.ProgressIndex, last, "near flow stays monotonic");
                last = p.ProgressIndex;
                p.Advance();
            }
            Assert.IsTrue(p.Finished, "sanity: the walk completed");
            Assert.Greater(last, 4, "the bar climbed past the skip");
        }
    }
}
