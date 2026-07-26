using System.Linq;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// The C# op registry, <see cref="StagingOps.Known"/>. (The file name is
    /// historical — these checks arrived with the camera-rig ops; the tests inside
    /// are about the registry, not the rig.)
    ///
    /// <para>The exhaustive check is table-driven on purpose. This set used to be
    /// pinned by <c>Assert.AreEqual(24, StagingOps.Known.Count)</c>, which is the
    /// worst kind of guard: it stayed green while the set fell six ops behind the
    /// language (<c>anim</c>, <c>input</c>, <c>load</c>, <c>save</c>, <c>text</c>,
    /// <c>wardrobe_show</c>) and would have gone red on the fix. A count remembers a
    /// number; what has to be asserted is the diff against the dictionary, so the
    /// test fails on drift and never on growth.</para>
    /// </summary>
    public class CameraRigTests
    {
        /// <summary>
        /// The registry must equal the shared op table row-for-row.
        /// <c>/conformance/ops-owners.json</c> carries one row per op in the
        /// reference dictionary and the Go guard (<c>TestOpOwnersCoverKnownOps</c>)
        /// pins that table to <c>lvn.KnownOps</c>, so diffing against it here is
        /// diffing against the language itself.
        /// </summary>
        [Test]
        public void StagingOpsMatchTheSharedOpTable()
        {
            var owners = ConformanceCorpus.Owners();
            if (owners == null) Assert.Ignore(ConformanceCorpus.Missing);

            var table = owners.Properties().Select(p => p.Name).ToList();
            var missing = table.Where(op => !StagingOps.Known.Contains(op)).OrderBy(op => op).ToList();
            var extra = StagingOps.Known.Where(op => !table.Contains(op)).OrderBy(op => op).ToList();

            Assert.IsEmpty(missing,
                "StagingOps.Known is missing ops the language defines: " + string.Join(", ", missing) +
                ". A host that asks this set \"is this a real op?\" gets a false negative for each of them. " +
                "Add them to Runtime/StagingOps.cs.");
            Assert.IsEmpty(extra,
                "StagingOps.Known lists ops that are not in the dictionary: " + string.Join(", ", extra) +
                ". Either the op was removed from lvn.KnownOps and this mirror kept it, or it is a host op " +
                "that belongs in LvnOps.Register, not in the language registry.");
        }

        /// <summary>
        /// A floor for consumers who install the UPM package without the repository:
        /// <c>/conformance</c> ships with the repo, so the table-driven test above
        /// ignores itself there. This one names one op of every kind — including the
        /// six the registry was silently missing — and only ever fails on removal, so
        /// it can never freeze the set the way the old count assertion did.
        /// </summary>
        [Test]
        public void StagingOpsCoverEveryKindOfOp()
        {
            var probes = new[]
            {
                "say", "choice", "goto", "if", "set", "inc", // flow and state
                "wait", "input", "preload", "load",          // player + stage
                "bg", "actor", "text", "audio", "anim",      // presentation
                "fade", "dim", "flash", "tint", "blur",
                "camera", "text_pace", "save",
                "wardrobe_show",                             // language op owned by the shell
            };
            foreach (var op in probes)
                Assert.IsTrue(StagingOps.Known.Contains(op),
                    "StagingOps.Known no longer knows the op \"" + op + "\" — the C# registry has drifted " +
                    "from lvn.KnownOps (tools/lvnconv/lvn/validate.go)");
        }
    }
}
