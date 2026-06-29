using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class TextInterpolationTests
    {
        private static Dictionary<string, JToken> Vars(params (string k, JToken v)[] kv)
        {
            var d = new Dictionary<string, JToken>();
            foreach (var p in kv) d[p.k] = p.v;
            return d;
        }

        [Test]
        public void Replaces_Single_Var()
        {
            Assert.AreEqual("Hi, Charlie!",
                TextInterpolation.Apply("Hi, {name}!", Vars(("name", "Charlie"))));
        }

        [Test]
        public void Replaces_Multiple_Vars()
        {
            Assert.AreEqual("score=42 hp=99",
                TextInterpolation.Apply("score={s} hp={h}", Vars(("s", 42), ("h", 99))));
        }

        [Test]
        public void Missing_Var_Renders_As_Literal_Placeholder()
        {
            Assert.AreEqual("hello {who}",
                TextInterpolation.Apply("hello {who}", Vars()));
        }

        [Test]
        public void Doubled_Braces_Escape()
        {
            Assert.AreEqual("{name} = Charlie",
                TextInterpolation.Apply("{{name}} = {name}", Vars(("name", "Charlie"))));
        }

        [Test]
        public void No_Braces_Returns_Original()
        {
            Assert.AreEqual("plain text", TextInterpolation.Apply("plain text", Vars(("name", "x"))));
        }

        [Test]
        public void Null_Or_Empty_Input_Roundtrips()
        {
            Assert.IsNull(TextInterpolation.Apply(null, Vars()));
            Assert.AreEqual("", TextInterpolation.Apply("", Vars()));
        }
    }
}
