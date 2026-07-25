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

        [SetUp]
        public void BareEngine() => LvnOps.Clear(); // no host ops: the public install

        [TearDown]
        public void Clean() => LvnOps.Clear();

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
            var doc = LvnDocument.Parse("{\"script\":[" + commandJson + ",{\"op\":\"say\",\"text\":\"дальше\"}]}");
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
                    break;

                case "shell-op":
                    // The gap this whole table exists to make visible: a bare
                    // com.lvn.engine host gets the command handed to a stage that has
                    // no case for it. Not a bug — a declared boundary.
                    Assert.IsTrue(forwarded,
                        "op " + op + " is shell-owned, so a bare engine can only forward it to the stage; " +
                        "it did not even do that");
                    break;

                default:
                    Assert.Fail("op " + op + ": unknown csharp dispatch site " + where);
                    break;
            }
        }

        /// <summary>
        /// The repository rule is "unknown is an error, never a silent skip", and the
        /// compiler holds it. The RUNTIME does not: an unregistered unknown op is
        /// forwarded to a stage that ignores it. This test pins that behaviour so it is
        /// a documented decision rather than a discovery — change the assertion the day
        /// the player starts refusing unknown ops.
        /// </summary>
        [Test]
        public void UnknownOpIsSilentlyForwarded()
        {
            var spy = Probe("{\"op\":\"no_such_op_anywhere\",\"x\":1}");
            Assert.AreEqual(1, spy.Forwarded.Count, "an unknown op should reach the stage exactly once");
            Assert.AreEqual("no_such_op_anywhere", (string)spy.Forwarded[0]["op"]);
            CollectionAssert.AreEqual(new[] { "дальше" }, spy.Lines,
                "the story must keep playing past an unknown op (today's contract)");
        }

        /// <summary>A host-registered op takes precedence over the silent forward —
        /// the sanctioned way to fill a gap like <c>wardrobe_show</c> without the shell.</summary>
        [Test]
        public void HostRegisteredOpIsHandledInsteadOfForwarded()
        {
            string seen = null;
            LvnOps.Register("wardrobe_show", (cmd, ctx) => seen = (string)cmd["char"]);
            var spy = Probe(Sample["wardrobe_show"]);
            Assert.AreEqual("hill", seen, "the host handler never ran");
            Assert.IsEmpty(spy.Forwarded, "a handled op must not also be forwarded to the stage");
        }
    }
}
