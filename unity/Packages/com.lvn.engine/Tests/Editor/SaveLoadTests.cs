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
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static LvnPlayer Play(string json, out SaveStage stage)
        {
            stage = new SaveStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
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
    }
}
