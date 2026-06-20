using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class SpriteComposerTests
    {
        private static CastEntity Mara(string json) => SpriteComposer.ParseCast(JObject.Parse(json))["mara"];

        private static Dictionary<string, string> Axes(params (string, string)[] kv)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Test]
        public void ResolvesWithDefaultsAndOverrides()
        {
            var e = Mara(@"{""mara"":{""name"":""Mara"",
                ""layers"":[""/a/body_{pose}.png"",""/a/face_{emotion}.png""],
                ""defaults"":{""pose"":""stand"",""emotion"":""neutral""}}}");
            var urls = SpriteComposer.Resolve(e, Axes(("emotion", "smile")));
            Assert.AreEqual(2, urls.Count);
            Assert.AreEqual("/a/body_stand.png", urls[0]); // default pose
            Assert.AreEqual("/a/face_smile.png", urls[1]); // overridden emotion
        }

        [Test]
        public void OptionalLayerSkippedWhenAxisMissing()
        {
            var e = Mara(@"{""mara"":{
                ""layers"":[""/a/body_{pose}.png"",""/a/{prop}.png""],
                ""defaults"":{""pose"":""stand""}}}");
            Assert.AreEqual(1, SpriteComposer.Resolve(e, Axes()).Count); // no prop → dropped
            var withProp = SpriteComposer.Resolve(e, Axes(("prop", "umbrella")));
            Assert.AreEqual(2, withProp.Count);
            Assert.AreEqual("/a/umbrella.png", withProp[1]);
        }

        [Test]
        public void StaticLayerNeedsNoAxis()
        {
            var e = Mara(@"{""mara"":{
                ""layers"":[""/a/shadow.png"",""/a/face_{emotion}.png""],
                ""defaults"":{""emotion"":""neutral""}}}");
            var urls = SpriteComposer.Resolve(e, Axes());
            Assert.AreEqual(2, urls.Count);
            Assert.AreEqual("/a/shadow.png", urls[0]);
            Assert.AreEqual("/a/face_neutral.png", urls[1]);
        }

        [Test]
        public void ParsesNamedEntities()
        {
            var map = SpriteComposer.ParseCast(JObject.Parse(@"{""mara"":{""name"":""Mara"",""layers"":[]}}"));
            Assert.IsTrue(map.ContainsKey("mara"));
            Assert.AreEqual("Mara", map["mara"].Name);
        }
    }
}
