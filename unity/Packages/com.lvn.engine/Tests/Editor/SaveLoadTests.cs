using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class SaveLoadTests
    {
        private sealed class SaveStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public string Last => Lines.Count > 0 ? Lines[Lines.Count - 1] : null;
            public IReadOnlyList<LvnOption> Options;

            public void ShowSay(string who, string text, string style)
                => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Options = options;
            // Подписанная дверь: заглушке различать отправителей незачем —
            // она просто записывает команду, как и раньше.
            public void ApplyStage(JObject command, Lvn.LvnSender sender) => ApplyStage(command);

            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static LvnPlayer Play(string json, out SaveStage stage)
        {
            stage = new SaveStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        // The random stream and the persist switch are process-wide; a test that
        // pins them must hand them back, or the next test inherits a rigged run.
        [TearDown]
        public void ResetRandomness()
        {
            LvnExpression.Random = new LvnRandom();
            LvnPlayer.PersistRandomState = true;
            LvnPlayer.Log = null;
        }

        // NOTE on Advance() granularity: one Advance() runs the script up to AND
        // INCLUDING the next pausing op (say/choice) — it shows that line, then
        // pauses. So the first Advance shows the first say; the cursor afterwards
        // points at the op following it. The expectations below follow that model.

        [Test]
        public void SaveCapturesState()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""health"",""value"":100},
                {""op"":""say"",""text"":""checkpoint""},
                {""op"":""say"",""text"":""next line""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set health=100, say "checkpoint" (cursor -> index 2)
            Assert.AreEqual("checkpoint", stage.Last);

            var snap = p.Save();
            Assert.AreEqual(2, snap.Index);
            Assert.AreEqual(100d, (double)snap.Vars["health"], 0.0001);
            Assert.IsNotNull(snap.CallStack);
        }

        [Test]
        public void RestoreResumesFromSnapshot()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""score"",""value"":0},
                {""op"":""inc"",""key"":""score"",""by"":10},
                {""op"":""say"",""text"":""before save""},
                {""op"":""say"",""text"":""after save""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set, inc, say "before save" (cursor -> index 3)
            Assert.AreEqual("before save", stage.Last);
            var snap = p.Save();
            Assert.AreEqual(3, snap.Index);

            p.Advance(); // say "after save"
            Assert.AreEqual("after save", stage.Last);

            p.Restore(snap);
            Assert.IsFalse(p.Finished);
            Assert.AreEqual(3, p.Index);

            p.Advance(); // resumes at index 3 -> say "after save" again
            Assert.AreEqual("after save", stage.Last);
        }

        [Test]
        public void RestoreWithVarsAndCallStack()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""flag"",""value"":true},
                {""op"":""call"",""label"":""sub""},
                {""op"":""say"",""text"":""main""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""sub""},
                {""op"":""say"",""text"":""subroutine""},
                {""op"":""return""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set flag, call sub, say "subroutine" (inside the call)
            Assert.AreEqual("subroutine", stage.Last);

            var snap = p.Save();
            Assert.AreEqual(true, (bool)snap.Vars["flag"]);
            Assert.Greater(snap.CallStack.Length, 0, "call stack should hold the return address");

            p.Advance(); // return -> say "main"
            Assert.AreEqual("main", stage.Last);

            // Restore lands back inside the subroutine with the call stack intact,
            // so the next Advance follows `return` back out to "main".
            p.Restore(snap);
            p.Advance();
            Assert.AreEqual("main", stage.Last);
        }

        [Test]
        public void MultipleSnapshotsAreIndependent()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""x"",""value"":1},
                {""op"":""say"",""text"":""first""},
                {""op"":""set"",""key"":""x"",""value"":2},
                {""op"":""say"",""text"":""second""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set x=1, say "first"
            Assert.AreEqual("first", stage.Last);
            var snap1 = p.Save();

            p.Advance(); // set x=2, say "second"
            Assert.AreEqual("second", stage.Last);
            var snap2 = p.Save();

            p.Restore(snap1);
            Assert.AreEqual(1d, (double)p.Vars["x"], 0.0001);

            p.Restore(snap2);
            Assert.AreEqual(2d, (double)p.Vars["x"], 0.0001);
        }

        // ── the save anchor across a content edit (audit O16) ────────────────
        //
        // The failure this pins: an author re-saves a chapter, the labels the
        // compiler minted come back under different names and every index shifts,
        // and the old code fell back to the raw saved index — landing the player in
        // a scene they had never reached, with nothing to say anything went wrong.

        // 0 label intro / 1 say / 2 label __nf1 / 3 say / 4 say / 5 label finale / 6 say
        private const string ChapterV1 = @"{""script"":[
            {""op"":""label"",""id"":""intro""},
            {""op"":""say"",""text"":""intro a""},
            {""op"":""label"",""id"":""__nf1""},
            {""op"":""say"",""text"":""intro b""},
            {""op"":""say"",""text"":""intro c""},
            {""op"":""label"",""id"":""finale""},
            {""op"":""say"",""text"":""finale a""}
        ]}";

        // Two Advances show "intro a" and "intro b"; the cursor then sits at 4,
        // under the minted label :__nf1, with "intro c" still to come.
        private static LvnPlayer.LvnSnapshot SaveMidIntro()
        {
            var p = Play(ChapterV1, out _);
            p.Advance();
            p.Advance();
            var snap = p.Save();
            Assert.AreEqual(4, snap.Index);
            Assert.AreEqual("__nf1", snap.AnchorLabel, "the nearest label is the anchor");
            Assert.AreEqual("intro", snap.AnchorStableLabel, "and the author's label is the shockproof one");
            return snap;
        }

        [Test]
        public void ResumeIsExactOnAnUntouchedChapter()
        {
            var snap = SaveMidIntro();
            var same = Play(ChapterV1, out var stage);
            same.Restore(snap);
            Assert.AreEqual(LvnPlayer.RestoreFidelity.Exact, same.LastRestore);
            Assert.AreEqual(snap.Index, same.Index);
            same.ContinueFrom(same.Index);
            Assert.AreEqual("intro c", stage.Last);
        }

        [Test]
        public void ResumeFollowsTheLabelWhenTheScriptGrows()
        {
            var snap = SaveMidIntro();
            // The same chapter with two lines added above the anchor: every index moved.
            var grown = Play(@"{""script"":[
                {""op"":""label"",""id"":""intro""},
                {""op"":""say"",""text"":""brand new opening""},
                {""op"":""say"",""text"":""and another""},
                {""op"":""say"",""text"":""intro a""},
                {""op"":""label"",""id"":""__nf1""},
                {""op"":""say"",""text"":""intro b""},
                {""op"":""say"",""text"":""intro c""},
                {""op"":""label"",""id"":""finale""},
                {""op"":""say"",""text"":""finale a""}
            ]}", out var stage);
            grown.Restore(snap);
            Assert.AreEqual(LvnPlayer.RestoreFidelity.Relocated, grown.LastRestore);
            grown.ContinueFrom(grown.Index);
            Assert.AreEqual("intro c", stage.Last, "the saved beat, at its new index");
        }

        [Test]
        public void ResumeFallsBackToTheAuthorLabelWhenTheMintedOneIsRenamed()
        {
            var snap = SaveMidIntro();
            // A re-save renamed the compiler's label AND dropped a line: the exact
            // anchor is gone, and the raw index 4 now points at :finale. The
            // author's own label has to catch the fall.
            var resaved = Play(@"{""script"":[
                {""op"":""label"",""id"":""intro""},
                {""op"":""say"",""text"":""intro a""},
                {""op"":""label"",""id"":""__nf_intro_1""},
                {""op"":""say"",""text"":""intro c""},
                {""op"":""label"",""id"":""finale""},
                {""op"":""say"",""text"":""finale a""}
            ]}", out var stage);
            resaved.Restore(snap);
            Assert.AreEqual(LvnPlayer.RestoreFidelity.Approximate, resaved.LastRestore);
            Assert.Less(resaved.Index, 4, "clamped inside :intro — never spilled into :finale");
            resaved.ContinueFrom(resaved.Index);
            Assert.AreEqual("intro c", stage.Last,
                "a resume must land in the scene the player was reading, never past it");
        }

        [Test]
        public void ResumeSaysTheChapterChangedInsteadOfGuessing()
        {
            var snap = SaveMidIntro();
            // The chapter was rewritten: not one of its labels survived, and the
            // raw index means nothing now.
            var rewritten = Play(@"{""script"":[
                {""op"":""label"",""id"":""prologue""},
                {""op"":""say"",""text"":""a whole new chapter""},
                {""op"":""say"",""text"":""with new scenes""},
                {""op"":""label"",""id"":""ending""},
                {""op"":""say"",""text"":""the end""}
            ]}", out var stage);
            rewritten.Restore(snap);
            Assert.AreEqual(LvnPlayer.RestoreFidelity.ChapterChanged, rewritten.LastRestore,
                "the host has to be able to tell the player the chapter changed");
            Assert.AreEqual(0, rewritten.Index, "restarted from the top, not dropped somewhere arbitrary");
            rewritten.ContinueFrom(rewritten.Index);
            Assert.AreEqual("a whole new chapter", stage.Last);
        }

        // ── the random stream is part of the save ────────────────────────────
        //
        // What this pins: rand()/chance() used to draw from a private static
        // System.Random — one per process, unseedable, absent from the snapshot.
        // Two consequences, both reproduced before the fix: a reload re-rolled
        // the fight the player had just lost (save-scumming built into the
        // engine), and a soak run pinned to a seed still walked a different path
        // every time, so the nightly flake hunter could not tell a flaky test
        // from content that simply rolled a different number.

        // Two rolls with a save between them. The range makes an accidental
        // match a one-in-a-million event.
        private const string TwoRolls = @"{""script"":[
            {""op"":""set"",""key"":""roll"",""expr"":""rand(1, 1000000)""},
            {""op"":""say"",""text"":""checkpoint""},
            {""op"":""set"",""key"":""roll2"",""expr"":""rand(1, 1000000)""},
            {""op"":""say"",""text"":""after""}
        ]}";

        [Test]
        public void ReloadReplaysTheSameRollInsteadOfReRollingIt()
        {
            LvnExpression.Random = new LvnRandom(2026);
            var p = Play(TwoRolls, out _);
            p.Advance();
            var snap = p.Save();
            Assert.IsNotNull(snap.RngState, "the snapshot has to carry the stream");

            p.Advance();
            var live = (long)p.Vars["roll2"];

            p.Restore(snap);
            p.ContinueFrom(p.Index);
            Assert.AreEqual(live, (long)p.Vars["roll2"],
                "a reload must replay the roll the player already saw, not re-roll it");
        }

        [Test]
        public void AnotherSessionRestoringTheSaveGetsTheSameRoll()
        {
            LvnExpression.Random = new LvnRandom(2026);
            var first = Play(TwoRolls, out _);
            first.Advance();
            var snap = first.Save();
            first.Advance();
            var live = (long)first.Vars["roll2"];

            // A different install / a later session: fresh player, fresh stream,
            // and the save round-tripped through JSON exactly as LvnSaveStore does.
            LvnExpression.Random = new LvnRandom(777);
            var wire = Newtonsoft.Json.JsonConvert.SerializeObject(snap);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnPlayer.LvnSnapshot>(wire);
            Assert.AreEqual(snap.RngState, back.RngState, "the stream has to survive JSON");

            var later = Play(TwoRolls, out _);
            later.Restore(back);
            later.ContinueFrom(later.Index);
            Assert.AreEqual(live, (long)later.Vars["roll2"]);
        }

        [Test]
        public void ASaveWrittenBeforeTheStreamExistedStillLoads()
        {
            // No RngState at all — every save on disk today. It must load, and it
            // must NOT reset the stream to some constant (that would make every
            // old save re-roll the same numbers); the live stream just keeps going,
            // which is exactly the behaviour those saves were written under.
            var old = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnPlayer.LvnSnapshot>(
                @"{""Index"":2,""Vars"":{""roll"":5},""CallStack"":[],""CommandCount"":4,""Finished"":false}");
            Assert.IsNull(old.RngState);

            LvnExpression.Random = new LvnRandom(555);
            var expected = new LvnRandom(555).NextInt(1, 1000000);

            var p = Play(TwoRolls, out _);
            Assert.DoesNotThrow(() => p.Restore(old));
            p.ContinueFrom(p.Index);
            Assert.AreEqual(expected, (long)p.Vars["roll2"],
                "an old save leaves the live stream running rather than rewinding it");
        }

        [Test]
        public void ACorruptStreamCostsARollNotTheLoad()
        {
            LvnExpression.Random = new LvnRandom(3);
            var p = Play(TwoRolls, out _);
            p.Advance();
            var snap = p.Save();
            snap.RngState = "lvnrng1:not-hex:at-all:0";

            var log = new List<string>();
            LvnPlayer.Log = log.Add;
            Assert.DoesNotThrow(() => p.Restore(snap));
            Assert.IsTrue(log.Exists(l => l.Contains("unreadable rng state")),
                "an unreadable stream has to be reported, not swallowed");
            p.ContinueFrom(p.Index);
            Assert.IsTrue(p.Vars.ContainsKey("roll2"), "and the chapter keeps playing");
        }

        [Test]
        public void AGameCanOptOutAndKeepTheOldReRollOnReload()
        {
            LvnPlayer.PersistRandomState = false;
            var p = Play(TwoRolls, out _);
            p.Advance();
            Assert.IsNull(p.Save().RngState);
        }

        [Test]
        public void ASeededStreamReplaysExactly()
        {
            var a = new LvnRandom(12345);
            var b = new LvnRandom(12345);
            for (int i = 0; i < 500; i++)
                Assert.AreEqual(a.NextULong(), b.NextULong(), "draw #" + i + " diverged");
            Assert.AreEqual(500, a.Draws);
            Assert.AreEqual(12345UL, a.Seed);
            Assert.AreNotEqual(new LvnRandom(12346).NextULong(), new LvnRandom(12345).NextULong());
        }

        [Test]
        public void TheStreamKeepsRandsPromises()
        {
            LvnExpression.Random = new LvnRandom(9);
            bool sawLo = false, sawHi = false;
            for (int i = 0; i < 5000; i++)
            {
                var v = (double)LvnExpression.Evaluate("rand(3, 7)", new Dictionary<string, JToken>());
                Assert.IsTrue(v >= 3 && v <= 7 && v == System.Math.Floor(v), "rand(3,7) → " + v);
                sawLo |= v == 3;
                sawHi |= v == 7;
                var d = (double)LvnExpression.Evaluate("rand()", new Dictionary<string, JToken>());
                Assert.IsTrue(d >= 0.0 && d < 1.0, "rand() → " + d);
            }
            Assert.IsTrue(sawLo && sawHi, "rand(a,b) is inclusive on both ends");
        }

        [Test]
        public void AnExplicitStreamLeavesTheAmbientOneAlone()
        {
            // The seam a host needs to run two stories at once without their
            // rolls interleaving.
            LvnExpression.Random = new LvnRandom(99);
            var mine = new LvnRandom(99);
            var vars = new Dictionary<string, JToken>();
            var fromMine = (double)LvnExpression.Evaluate("rand(0, 1000000)", vars, mine);
            var fromAmbient = (double)LvnExpression.Evaluate("rand(0, 1000000)", vars);
            Assert.AreEqual(fromMine, fromAmbient, 0.0,
                "the ambient stream should not have advanced while the explicit one drew");
        }

        [Test]
        public void RollbackRewindsTheRollToo()
        {
            // Each beat's snapshot holds the stream as it was BEFORE the beat, so
            // stepping back and replaying re-draws the same numbers instead of
            // handing the player a fresh lottery ticket per rollback.
            LvnExpression.Random = new LvnRandom(4242);
            var p = Play(TwoRolls, out _);
            p.Advance();
            var beat = p.Save();
            p.Advance();
            var first = (long)p.Vars["roll2"];
            p.Restore(beat);
            p.ContinueFrom(p.Index);
            Assert.AreEqual(first, (long)p.Vars["roll2"]);
        }
    }
}
