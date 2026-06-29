using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Pins chapter resume-point resolution — core progress mechanics. A wrong
    /// branch either replays seen content or discards progress + player name.
    public class ResumePlannerTests
    {
        [Test]
        public void NoSlot_StartsFresh()
        {
            var p = ResumePlanner.Resolve(hasSlot: false, finished: false, sameScript: false,
                savedIndex: 50, savedCommandCount: 100, currentCommandCount: 100, lastEditAt: null);
            Assert.AreEqual(0, p.StartIndex);
            Assert.IsFalse(p.RestoreState);
            Assert.IsFalse(p.LoadVars);
        }

        [Test]
        public void Finished_StartsFresh()
        {
            var p = ResumePlanner.Resolve(true, finished: true, sameScript: true,
                savedIndex: 50, savedCommandCount: 100, currentCommandCount: 100, lastEditAt: null);
            Assert.AreEqual(0, p.StartIndex);
            Assert.IsFalse(p.LoadVars);
        }

        [Test]
        public void DifferentScript_StartsFresh()
        {
            var p = ResumePlanner.Resolve(true, finished: false, sameScript: false,
                savedIndex: 50, savedCommandCount: 100, currentCommandCount: 100, lastEditAt: null);
            Assert.AreEqual(0, p.StartIndex);
            Assert.IsFalse(p.LoadVars);
        }

        [Test]
        public void UnchangedScript_ResumesExactly()
        {
            var p = ResumePlanner.Resolve(true, false, true,
                savedIndex: 42, savedCommandCount: 100, currentCommandCount: 100, lastEditAt: null);
            Assert.AreEqual(42, p.StartIndex);
            Assert.IsTrue(p.RestoreState);
            Assert.IsTrue(p.LoadVars);
        }

        [Test]
        public void EditBeforeSavedIndex_RewindsToEditPoint()
        {
            var p = ResumePlanner.Resolve(true, false, true,
                savedIndex: 80, savedCommandCount: 100, currentCommandCount: 105, lastEditAt: 30);
            Assert.AreEqual(30, p.StartIndex);
            Assert.IsTrue(p.RestoreState);
            Assert.IsTrue(p.LoadVars);
        }

        [Test]
        public void EditAfterSavedIndex_KeepsSavedIndex_WhenCountAlsoChanged()
        {
            var p = ResumePlanner.Resolve(true, false, true,
                savedIndex: 40, savedCommandCount: 100, currentCommandCount: 120, lastEditAt: 90);
            Assert.AreEqual(40, p.StartIndex);
        }

        [Test]
        public void LengthChangedNoEditPoint_ClampsIntoShorterScript()
        {
            var p = ResumePlanner.Resolve(true, false, true,
                savedIndex: 90, savedCommandCount: 100, currentCommandCount: 50, lastEditAt: null);
            Assert.AreEqual(49, p.StartIndex);  // Clamp(90, 0, 49)
            Assert.IsTrue(p.LoadVars);          // progress/name preserved
        }

        [Test]
        public void LengthChangedEditPointZero_TreatedAsUnknown_Clamps()
        {
            var p = ResumePlanner.Resolve(true, false, true,
                savedIndex: 30, savedCommandCount: 100, currentCommandCount: 80, lastEditAt: 0);
            Assert.AreEqual(30, p.StartIndex);
        }

        [Test]
        public void ResumeAtZero_DoesNotRestoreStage_ButStillLoadsVars()
        {
            var p = ResumePlanner.Resolve(true, false, true,
                savedIndex: 0, savedCommandCount: 100, currentCommandCount: 100, lastEditAt: null);
            Assert.AreEqual(0, p.StartIndex);
            Assert.IsFalse(p.RestoreState);
            Assert.IsTrue(p.LoadVars);
        }
    }
}
