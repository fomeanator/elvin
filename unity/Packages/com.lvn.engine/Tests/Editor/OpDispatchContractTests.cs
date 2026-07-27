using System.Collections.Generic;
using System.Linq;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Behavioural half of the op-dispatch contract: for every op in the shared
    /// ownership table (<c>/conformance/ops-owners.json</c>), does the player really
    /// dispatch it where the table says?
    ///
    /// <para>The table's other half — that a handler exists at all in
    /// <c>VnStage.ApplyStage</c> / the shell's <c>LvnOps.Register</c> — is pinned in Go
    /// (<c>tools/lvnconv/lvn/conformance_test.go</c>), which reads the dispatch sites
    /// straight from the sources and needs no editor. What only a running player can
    /// prove is the split itself: a flow op must be CONSUMED (never leak to the stage,
    /// where it would silently stop mutating state), and a staging op must be
    /// FORWARDED verbatim (fields intact, or the renderer draws the wrong thing).</para>
    ///
    /// <para>The suite runs against a BARE engine — no shell, no host ops registered —
    /// which is exactly the public UPM install from the README. That is why
    /// <c>wardrobe_show</c> shows up here as a forward-and-nothing-else.</para>
    /// </summary>
    public class OpDispatchContractTests
    {
        // A one-command sample per op, with just enough fields to be a legal command.
        // Kept here rather than in the corpus: these are not stories, they are probes.
        static readonly Dictionary<string, string> Sample = new Dictionary<string, string>
        {
            { "say", "{\"op\":\"say\",\"text\":\"проба\"}" },
            { "choice", "{\"op\":\"choice\",\"options\":[{\"text\":\"a\",\"goto\":\"__end\"}]}" },
            { "label", "{\"op\":\"label\",\"id\":\"probe\"}" },
            { "goto", "{\"op\":\"goto\",\"label\":\"__end\"}" },
            { "if", "{\"op\":\"if\",\"expr\":\"1\",\"then\":\"__end\",\"else\":\"__end\"}" },
            { "call", "{\"op\":\"call\",\"label\":\"__end\"}" },
            { "return", "{\"op\":\"return\"}" },
            { "set", "{\"op\":\"set\",\"key\":\"probe\",\"value\":1}" },
            { "inc", "{\"op\":\"inc\",\"key\":\"probe\"}" },
            { "wait", "{\"op\":\"wait\",\"ms\":1}" },
            { "input", "{\"op\":\"input\",\"var\":\"probe\"}" },
            { "preload", "{\"op\":\"preload\",\"assets\":[]}" },
            { "load", "{\"op\":\"load\",\"slot\":\"probe\"}" },
            { "bg", "{\"op\":\"bg\",\"id\":\"b\",\"sprite_url\":\"bg/b.png\"}" },
            { "actor", "{\"op\":\"actor\",\"id\":\"a\",\"emotion\":\"calm\"}" },
            { "obj", "{\"op\":\"obj\",\"id\":\"o\"}" },
            { "text", "{\"op\":\"text\",\"id\":\"hud\",\"text\":\"x\"}" },
            { "audio", "{\"op\":\"audio\",\"channel\":\"music\",\"url\":\"a.ogg\"}" },
            { "fade", "{\"op\":\"fade\",\"to\":\"black\",\"duration\":0.5}" },
            { "dim", "{\"op\":\"dim\",\"alpha\":0.5}" },
            { "tint", "{\"op\":\"tint\",\"color\":\"red\",\"alpha\":0.3}" },
            { "flash", "{\"op\":\"flash\",\"color\":\"white\"}" },
            { "blur", "{\"op\":\"blur\",\"alpha\":0.4}" },
            { "camera", "{\"op\":\"camera\",\"action\":\"shake\",\"amplitude\":2}" },
            { "particles", "{\"op\":\"particles\",\"type\":\"rain\",\"on\":true}" },
            { "anim", "{\"op\":\"anim\",\"id\":\"a\",\"anim\":\"wave\"}" },
            { "clear", "{\"op\":\"clear\"}" },
            { "text_pace", "{\"op\":\"text_pace\",\"cps\":30}" },
            { "hint", "{\"op\":\"hint\",\"text\":\"подсказка\"}" },
            { "save", "{\"op\":\"save\",\"slot\":\"probe\"}" },
            { "wardrobe_show", "{\"op\":\"wardrobe_show\",\"char\":\"hill\"}" },
        };

        sealed class ForwardSpy : ILvnStage
        {
            public readonly List<JObject> Forwarded = new List<JObject>();
            public readonly List<string> Lines = new List<string>();
            public bool ChoiceShown, Ended;

            public void ShowSay(string who, string text, string style) => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => ChoiceShown = true;
            public void ApplyStage(JObject command) => Forwarded.Add(command);
            public void OnEnd() => Ended = true;
        }

        // Every case starts from the public install AND from a clean diagnostic
        // slate. The unclaimed-op report is process-wide by design (one line per
        // op per SESSION), so without the reset the second case to touch a given
        // op would see no warning and pass for entirely the wrong reason.
        readonly List<string> _warnings = new List<string>();
        System.Action<string> _warnBefore;

        [SetUp]
        public void BareEngine()
        {
            LvnOps.Clear(); // no host ops: the public install
            _warnBefore = LvnPlayer.Warn;
            Assert.NotNull(_warnBefore,
                "LvnPlayer.Warn lost its default sink — an unclaimed op would then be invisible in a real " +
                "build, which is the exact silence this suite exists to prevent");
            _warnings.Clear();
            LvnPlayer.Warn = _warnings.Add;
            LvnPlayer.ResetOpDiagnostics();
        }

        [TearDown]
        public void Clean()
        {
            LvnOps.Clear();
            LvnPlayer.Warn = _warnBefore;
            LvnPlayer.ResetOpDiagnostics();
        }

        static IEnumerable<string> OpNames()
        {
            var owners = ConformanceCorpus.Owners();
            if (owners == null) return new[] { "(no table)" };
            var names = owners.Properties().Select(p => p.Name).ToList();
            names.Sort(System.StringComparer.Ordinal);
            return names;
        }

        static ForwardSpy Probe(string commandJson)
        {
            var spy = new ForwardSpy();
            // The scene name is carried so the probe exercises what a real chapter
            // has — the unclaimed-op report quotes it, and "which chapter?" is the
            // first thing a reader of that line needs.
            var doc = LvnDocument.Parse(
                "{\"scene\":\"conformance\",\"script\":[" + commandJson + ",{\"op\":\"say\",\"text\":\"дальше\"}]}");
            new LvnPlayer(doc, spy).Advance();
            return spy;
        }

        [Test]
        public void OpDispatchesWhereTheTableSaysItDoes([ValueSource(nameof(OpNames))] string op)
        {
            if (op == "(no table)") Assert.Ignore(ConformanceCorpus.Missing);

            var row = (JObject)ConformanceCorpus.Owners()[op];
            Assert.IsTrue(Sample.TryGetValue(op, out var json),
                "op " + op + " is in /conformance/ops-owners.json but this test has no probe command for it — " +
                "add one to Sample so the new op's dispatch is actually checked");

            var spy = Probe(json);
            bool forwarded = spy.Forwarded.Any(c => (string)c["op"] == op);
            var where = (string)row["csharp"];

            switch (where)
            {
                case "player":
                    Assert.IsFalse(forwarded,
                        "op " + op + " is declared csharp=player but the player handed it to the stage — " +
                        "it lost its case and is now falling through default:, so whatever it was supposed to do " +
                        "(mutate a variable, move the cursor) silently no longer happens");
                    CollectionAssert.IsEmpty(_warnings,
                        "op " + op + " is consumed by the player and must never be reported as unclaimed");
                    break;

                case "stage":
                case "player+stage":
                    Assert.IsTrue(forwarded,
                        "op " + op + " is declared csharp=" + where + " but never reached ILvnStage — " +
                        "nothing can draw it");
                    // Verbatim: the renderer reads its fields off the raw command, so a
                    // rewritten or rebuilt command is a silently wrong picture.
                    var got = spy.Forwarded.First(c => (string)c["op"] == op);
                    Assert.IsTrue(JToken.DeepEquals(JObject.Parse(json), got),
                        "op " + op + " reached the stage altered: expected " + json + ", got " + got.ToString(Newtonsoft.Json.Formatting.None));
                    // Drift guard for the other half of the diagnostic: LvnPlayer
                    // keeps a private list of the ops the ENGINE forwards, and
                    // warns about anything else. Add an engine staging op to this
                    // table without adding it there and every single one of its
                    // commands starts crying "unclaimed" in a working build.
                    CollectionAssert.IsEmpty(_warnings,
                        "op " + op + " is engine-owned and really is drawn by VnStage, but the player reported it " +
                        "as UNCLAIMED — LvnPlayer._engineStageOps has fallen behind conformance/ops-owners.json; " +
                        "add \"" + op + "\" to it");
                    break;

                case "shell-op":
                    // The gap this whole table exists to make visible: a bare
                    // com.lvn.engine host gets the command handed to a stage that has
                    // no case for it. Not a bug — a declared boundary.
                    Assert.IsTrue(forwarded,
                        "op " + op + " is shell-owned, so a bare engine can only forward it to the stage; " +
                        "it did not even do that");
                    // …and the boundary is now AUDIBLE. The declared gap was the
                    // one place a player could lose a scripted beat with nothing
                    // anywhere to show for it.
                    Assert.AreEqual(1, _warnings.Count,
                        "op " + op + " is shell-owned and this is a bare engine, so it was ignored — that must be " +
                        "reported exactly once (got " + _warnings.Count + ")");
                    StringAssert.Contains(op, _warnings[0]);
                    break;

                default:
                    Assert.Fail("op " + op + ": unknown csharp dispatch site " + where);
                    break;
            }
        }

        /// <summary>
        /// The repository rule is "unknown is an error, never a silent skip" — and the
        /// README scopes it: "content bugs surface at COMPILE time". So the runtime
        /// keeps playing past an op nobody handles (throwing out of Advance, which is
        /// driven by tap/auto-advance/choice handlers, would soft-lock the game in a
        /// player's hands), but it no longer keeps QUIET about it.
        ///
        /// <para>This test is the two-sided pin. It fails if the diagnostic regresses
        /// to silence, it fails if the runtime starts refusing the op, and it fails if
        /// the diagnostic becomes a per-command stream — the failure mode that would
        /// get the whole thing deleted the first time someone tails a device log.</para>
        /// </summary>
        [Test]
        public void UnclaimedOpIsForwardedAndReportedExactlyOnce()
        {
            var spy = Probe("{\"op\":\"no_such_op_anywhere\",\"x\":1}");

            // 1. Forwarding is unchanged: verbatim, once, and the story rolls on.
            Assert.AreEqual(1, spy.Forwarded.Count, "an unclaimed op should reach the stage exactly once");
            Assert.AreEqual("no_such_op_anywhere", (string)spy.Forwarded[0]["op"]);
            CollectionAssert.AreEqual(new[] { "дальше" }, spy.Lines,
                "the story must keep playing past an unclaimed op — refusing it soft-locks the player, " +
                "because Advance() is called from input handlers with nothing to catch");

            // 2. …but it is no longer silent, and the line is ACTIONABLE: a reader
            //    must learn the op, where it was, and what to do, without opening
            //    a single source file.
            Assert.AreEqual(1, _warnings.Count,
                "an unclaimed op must be reported exactly once — 0 means the runtime went back to a silent " +
                "skip, more than 1 means the once-per-op budget broke");
            var line = _warnings[0];
            StringAssert.Contains("no_such_op_anywhere", line, "the report must name the op");
            StringAssert.Contains("#0", line, "the report must give the command index");
            StringAssert.Contains("scene 'conformance'", line, "the report must name the chapter it happened in");
            StringAssert.Contains("LvnOps.Register", line, "the report must name the fix for a host op");
            StringAssert.Contains("ext-grammar.json", line, "the report must name the way to declare it to lvnconv");

            // 3. Not a stream. Three more of the same op, in a fresh document and a
            //    fresh player, add nothing to the log — the budget is per op per
            //    SESSION, not per command, per chapter or per replay.
            var again = Probe(
                "{\"op\":\"no_such_op_anywhere\"},{\"op\":\"no_such_op_anywhere\"},{\"op\":\"no_such_op_anywhere\"}");
            Assert.AreEqual(3, again.Forwarded.Count, "every occurrence still reaches the stage");
            Assert.AreEqual(1, _warnings.Count,
                "the unclaimed-op report must stay at ONE line per op per session — a per-command warning " +
                "drowns the device log and puts string formatting in the player's inner loop");

            // 4. The count keeps running even though the log does not, so a host can
            //    still see how much was dropped.
            Assert.AreEqual(4, LvnPlayer.UnclaimedOps["no_such_op_anywhere"],
                "every unclaimed command must be counted, not just the reported one");
        }

        /// <summary>A host-registered op takes precedence over the forward — the
        /// sanctioned way to fill a gap like <c>wardrobe_show</c> without the shell.
        /// It must also be silent: a handled op is not a gap, and a false alarm here
        /// is what would train people to ignore the real ones.</summary>
        [Test]
        public void HostRegisteredOpIsHandledInsteadOfForwarded()
        {
            string seen = null;
            LvnOps.Register("wardrobe_show", (cmd, ctx) => seen = (string)cmd["char"]);
            var spy = Probe(Sample["wardrobe_show"]);
            Assert.AreEqual("hill", seen, "the host handler never ran");
            Assert.IsEmpty(spy.Forwarded, "a handled op must not also be forwarded to the stage");
            CollectionAssert.IsEmpty(_warnings, "a handled op must not be reported as unclaimed");
        }

        /// <summary>
        /// The registration race, which is the one way this diagnostic can cry wolf on
        /// a healthy game: Unity does not order two MonoBehaviours' <c>Start</c>, so a
        /// host that registers its ops there can lose to the runner that starts the
        /// chapter. The transient log line is capped at one and names that cause — but
        /// the persistent artifact, <see cref="LvnPlayer.UnclaimedOps"/>, must not keep
        /// accusing an op that turned out to be handled after all.
        /// </summary>
        [Test]
        public void LateRegistrationClearsTheOpFromTheReport()
        {
            Probe("{\"op\":\"late_op\"}");
            Assert.IsTrue(LvnPlayer.UnclaimedOps.ContainsKey("late_op"),
                "an op that ran before its handler existed is a real gap while it lasts");

            LvnOps.Register("late_op", (cmd, ctx) => { }); // the host's Start finally ran
            Assert.IsFalse(LvnPlayer.UnclaimedOps.ContainsKey("late_op"),
                "once the handler exists the op is handled — leaving it in the report turns a boot-order " +
                "race into a permanent false accusation, and 'is the report empty?' stops being a usable check");
        }
    }
}
