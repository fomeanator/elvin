using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class LvnExpressionTests
    {
        private static Dictionary<string, JToken> Vars(params (string key, JToken value)[] kv)
        {
            var d = new Dictionary<string, JToken>();
            foreach (var (key, value) in kv) d[key] = value;
            return d;
        }

        // The regression that broke once-only choices: an unset variable must
        // compare equal to 0 (ink defaulting) so `__once == 0` is true on the
        // first visit.
        [Test]
        public void UnsetVariableEqualsZero()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("__once == 0", Vars()));
        }

        [Test]
        public void SetVariableClosesOnceGate()
        {
            Assert.IsFalse(LvnExpression.EvaluateBool("__once == 0", Vars(("__once", 1))));
        }

        [Test]
        public void Functions_PureMath()
        {
            Assert.AreEqual(2.0, (double)LvnExpression.Evaluate("min(2, 5)", Vars()), 0.001);
            Assert.AreEqual(5.0, (double)LvnExpression.Evaluate("max(2, 5)", Vars()), 0.001);
            Assert.AreEqual(3.0, (double)LvnExpression.Evaluate("abs(-3)", Vars()), 0.001);
            Assert.AreEqual(2.0, (double)LvnExpression.Evaluate("floor(2.9)", Vars()), 0.001);
            Assert.AreEqual(3.0, (double)LvnExpression.Evaluate("round(2.6)", Vars()), 0.001);
        }

        [Test]
        public void Rand_InclusiveIntegerRange()
        {
            for (int i = 0; i < 200; i++)
            {
                var v = (double)LvnExpression.Evaluate("rand(3, 7)", Vars());
                Assert.GreaterOrEqual(v, 3);
                Assert.LessOrEqual(v, 7);
                Assert.AreEqual(v, System.Math.Floor(v), "rand(a,b) is an integer");
            }
            Assert.AreEqual(5.0, (double)LvnExpression.Evaluate("rand(5, 5)", Vars()), 0.001);
        }

        [Test]
        public void Chance_ZeroAndOne()
        {
            Assert.IsFalse(LvnExpression.EvaluateBool("chance(0)", Vars()));
            Assert.IsTrue(LvnExpression.EvaluateBool("chance(1)", Vars()));
        }

        [Test]
        public void RandUsableInDamageExpression()
        {
            var v = (double)LvnExpression.Evaluate("ghp - rand(4, 7)", Vars(("ghp", 18)));
            Assert.GreaterOrEqual(v, 11);
            Assert.LessOrEqual(v, 14);
        }

        // ── collections: lists & maps (inventory RPG core) ──

        [Test]
        public void ListLiteralIndexAndLen()
        {
            Assert.AreEqual(3.0, (double)LvnExpression.Evaluate("len([10, 20, 30])", Vars()), 0.001);
            Assert.AreEqual(20L, (long)LvnExpression.Evaluate("[10, 20, 30][1]", Vars()));
            // out-of-range reads as null → 0 in numeric context
            Assert.IsTrue(LvnExpression.EvaluateBool("[1,2][5] == 0", Vars()));
        }

        [Test]
        public void MapLiteralMemberAndGet()
        {
            Assert.AreEqual(3L, (long)LvnExpression.Evaluate("{ potion: 3, key: 1 }.potion", Vars()));
            Assert.AreEqual(1L, (long)LvnExpression.Evaluate("{ potion: 3, key: 1 }[\"key\"]", Vars()));
            Assert.AreEqual(7L, (long)LvnExpression.Evaluate("get({ a: 1 }, \"missing\", 7)", Vars()));
        }

        [Test]
        public void HasOnListAndMap()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("has([\"sword\", \"shield\"], \"sword\")", Vars()));
            Assert.IsFalse(LvnExpression.EvaluateBool("has([\"sword\"], \"bow\")", Vars()));
            Assert.IsTrue(LvnExpression.EvaluateBool("has({ gold: 5 }, \"gold\")", Vars()));
        }

        [Test]
        public void PureBuildersDoNotMutateAndChain()
        {
            // push returns a new list; the source var is untouched
            var inv = new JArray { "sword" };
            var vars = Vars(("inv", inv));
            var pushed = (JArray)LvnExpression.Evaluate("push(inv, \"potion\")", vars);
            Assert.AreEqual(2, pushed.Count);
            Assert.AreEqual(1, ((JArray)vars["inv"]).Count, "push must not mutate the source list");
            Assert.AreEqual(1L, (long)LvnExpression.Evaluate("len(removeat(push(inv, \"potion\"), 0))", vars));
        }

        [Test]
        public void MapPutCountThenRead()
        {
            // the canonical inventory bump: bag = put(bag, "potion", get(bag,"potion",0)+1)
            var vars = Vars(("bag", new JObject()));
            var bumped = (JObject)LvnExpression.Evaluate("put(bag, \"potion\", get(bag, \"potion\", 0) + 1)", vars);
            Assert.AreEqual(1L, (long)bumped["potion"]);
            vars["bag"] = bumped;
            var bumped2 = (JObject)LvnExpression.Evaluate("put(bag, \"potion\", get(bag, \"potion\", 0) + 1)", vars);
            Assert.AreEqual(2L, (long)bumped2["potion"]);
        }

        [Test]
        public void SumAndKeysOverCollections()
        {
            Assert.AreEqual(6.0, (double)LvnExpression.Evaluate("sum([1, 2, 3])", Vars()), 0.001);
            Assert.AreEqual(2L, (long)LvnExpression.Evaluate("len(keys({ a: 1, b: 2 }))", Vars()));
            Assert.AreEqual(1L, (long)LvnExpression.Evaluate("indexof([\"a\", \"b\"], \"b\")", Vars()));
        }

        [Test]
        public void UnsetVariableEqualsEmptyStringAndFalse()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("name == \"\"", Vars()));
            Assert.IsTrue(LvnExpression.EvaluateBool("flag == false", Vars()));
        }

        [Test]
        public void BooleanAndComparisonOperators()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("courage >= 2 && !lied", Vars(("courage", 2))));
            Assert.IsFalse(LvnExpression.EvaluateBool("courage >= 2 && !lied", Vars(("courage", 2), ("lied", true))));
        }

        [Test]
        public void Arithmetic()
        {
            Assert.AreEqual(6L, (long)LvnExpression.Evaluate("(1 + 2) * 2", Vars()));
        }

        [Test]
        public void StringEquality()
        {
            Assert.IsTrue(LvnExpression.EvaluateBool("name == \"Mara\"", Vars(("name", "Mara"))));
            Assert.IsFalse(LvnExpression.EvaluateBool("name == \"Mara\"", Vars(("name", "Kel"))));
        }

        [Test]
        public void VisitCountComparisonOnUnset()
        {
            // gte/lt go through AsNum, where null is already 0 — pin it.
            Assert.IsFalse(LvnExpression.EvaluateBool("__seen >= 1", Vars()));
            Assert.IsTrue(LvnExpression.EvaluateBool("__seen >= 1", Vars(("__seen", 1))));
        }
    }
}
