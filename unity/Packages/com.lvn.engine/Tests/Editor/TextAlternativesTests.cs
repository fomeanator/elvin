using System;
using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Pins ink-style text alternatives in dialogue: conditional text, sequences,
    /// cycles, once-only and shuffles, with counters living in the player's Vars
    /// (so they save/resume like everything else).
    public class TextAlternativesTests
    {
        private Dictionary<string, JToken> _vars;

        [SetUp]
        public void SetUp() => _vars = new Dictionary<string, JToken>();

        private string Apply(string s, int site = 7, Random rng = null) =>
            TextAlternatives.Apply(s, _vars, site, rng);

        [Test]
        public void PlainVarPlaceholder_IsLeftForInterpolation()
        {
            Assert.AreEqual("Hi, {player_name}!", Apply("Hi, {player_name}!"));
        }

        [Test]
        public void EscapedBraces_AreLeftAlone()
        {
            Assert.AreEqual("a {{literal}} b", Apply("a {{literal}} b"));
        }

        [Test]
        public void Conditional_PicksBranchByExpr()
        {
            _vars["met"] = true;
            Assert.AreEqual("Again.", Apply("{met: Again.|Who are you?}"));
            _vars["met"] = false;
            Assert.AreEqual("Who are you?", Apply("{met: Again.|Who are you?}"));
        }

        [Test]
        public void Conditional_WithoutElse_EmptyWhenFalse()
        {
            _vars["angry"] = false;
            Assert.AreEqual("She looked at you. ", Apply("She looked at you. {angry: Coldly.}"));
        }

        [Test]
        public void Conditional_CompoundExpr()
        {
            _vars["courage"] = 3;
            _vars["lied"] = false;
            Assert.AreEqual("hero", Apply("{courage >= 2 && !lied: hero|coward}"));
        }

        [Test]
        public void Sequence_AdvancesAndStopsOnLast()
        {
            const string t = "{one|two|three}";
            Assert.AreEqual("one", Apply(t));
            Assert.AreEqual("two", Apply(t));
            Assert.AreEqual("three", Apply(t));
            Assert.AreEqual("three", Apply(t));
        }

        [Test]
        public void Cycle_Loops()
        {
            const string t = "{&a|b}";
            Assert.AreEqual("a", Apply(t));
            Assert.AreEqual("b", Apply(t));
            Assert.AreEqual("a", Apply(t));
        }

        [Test]
        public void Once_ThenEmpty()
        {
            const string t = "{!only once}";
            Assert.AreEqual("only once", Apply(t));
            Assert.AreEqual("", Apply(t));
        }

        [Test]
        public void Shuffle_UsesInjectedRng()
        {
            var rng = new Random(42);
            var seen = new HashSet<string>();
            for (int i = 0; i < 20; i++) seen.Add(Apply("{~a|b|c}", rng: rng));
            CollectionAssert.IsSubsetOf(seen, new[] { "a", "b", "c" });
            Assert.Greater(seen.Count, 1, "shuffle should vary");
        }

        [Test]
        public void CountersAreSiteScoped_AndPersistInVars()
        {
            Assert.AreEqual("one", Apply("{one|two}", site: 1));
            Assert.AreEqual("one", Apply("{one|two}", site: 2)); // different site — own counter
            Assert.AreEqual("two", Apply("{one|two}", site: 1));
            Assert.IsTrue(_vars.ContainsKey("__alt_1_0"), "counter must live in Vars for save/resume");
        }

        [Test]
        public void NestedAlternatives_ExpandRecursively()
        {
            _vars["met"] = true;
            Assert.AreEqual("Again one", Apply("{met: Again {one|two}|No}"));
            Assert.AreEqual("Again two", Apply("{met: Again {one|two}|No}"));
        }

        [Test]
        public void MultipleBlocksInOneLine_GetDistinctCounters()
        {
            Assert.AreEqual("a and x", Apply("{a|b} and {x|y}"));
            Assert.AreEqual("b and y", Apply("{a|b} and {x|y}"));
        }

        [Test]
        public void MalformedExprBlock_LeftLiteral_NeverThrows()
        {
            // A smiley reads as `{<empty expr>: -)}` — must not throw out of the
            // player (which would abort the chapter); left as literal instead.
            Assert.AreEqual("oh {:-)} ok", Apply("oh {:-)} ok"));
        }
    }
}
