using System.Collections.Generic;
using Lvn.Content;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class SpriteCatalogTests
    {
        private static SpriteCatalog Cat(params (string id, LvnSpriteEntity e)[] items)
        {
            var d = new Dictionary<string, LvnSpriteEntity>();
            foreach (var (id, e) in items) d[id] = e;
            return new SpriteCatalog(d);
        }

        private static LvnSpriteEntity Entity(List<LvnLayer> layers, Dictionary<string, string> defaults = null)
            => new LvnSpriteEntity { layers = layers, defaults = defaults };

        [Test]
        public void Resolve_SimpleSprite_SingleUrl()
        {
            var cat = Cat(("porch", Entity(new List<LvnLayer> { new LvnLayer("/bg/porch.jpg") })));
            var urls = cat.Resolve("porch", null);
            Assert.AreEqual(1, urls.Count);
            Assert.AreEqual("/bg/porch.jpg", urls[0]);
        }

        [Test]
        public void Resolve_FillsAxesOverDefaults()
        {
            var cat = Cat(("mara", Entity(
                new List<LvnLayer>
                {
                    new LvnLayer("/a/body_{pose}.png"),
                    new LvnLayer("/a/face_{emotion}.png"),
                },
                new Dictionary<string, string> { { "pose", "standing" }, { "emotion", "neutral" } })));

            var def = cat.Resolve("mara", null);
            Assert.AreEqual("/a/body_standing.png", def[0]);
            Assert.AreEqual("/a/face_neutral.png", def[1]);

            var over = cat.Resolve("mara", new Dictionary<string, string> { { "pose", "sitting" }, { "emotion", "smile" } });
            Assert.AreEqual("/a/body_sitting.png", over[0]);
            Assert.AreEqual("/a/face_smile.png", over[1]);
        }

        [Test]
        public void Resolve_UnresolvedTokenDropsLayer()
        {
            var cat = Cat(("x", Entity(new List<LvnLayer>
            {
                new LvnLayer("/a/base.png"),
                new LvnLayer("/a/opt_{missing}.png"),
            })));
            var urls = cat.Resolve("x", null);
            Assert.AreEqual(1, urls.Count);
            Assert.AreEqual("/a/base.png", urls[0]);
        }

        [Test]
        public void Resolve_ConditionalLayer_ShownOnlyWhenConditionHolds()
        {
            var cat = Cat(("mara", Entity(new List<LvnLayer>
            {
                new LvnLayer("/a/body.png"),
                new LvnLayer("/a/blush.png", when: "warmth >= 1"),
            })));

            var off = cat.Resolve("mara", null, cond: _ => false);
            Assert.AreEqual(1, off.Count, "conditional layer hidden when condition is false");

            var on = cat.Resolve("mara", null, cond: expr => { Assert.AreEqual("warmth >= 1", expr); return true; });
            Assert.AreEqual(2, on.Count, "conditional layer shown when condition is true");
            Assert.AreEqual("/a/blush.png", on[1]);
        }

        [Test]
        public void Resolve_NullCondShowsAllConditionalLayers()
        {
            var cat = Cat(("mara", Entity(new List<LvnLayer>
            {
                new LvnLayer("/a/body.png"),
                new LvnLayer("/a/blush.png", when: "warmth >= 1"),
            })));
            Assert.AreEqual(2, cat.Resolve("mara", null, cond: null).Count);
        }

        [Test]
        public void Resolve_UnknownIdIsEmpty()
        {
            var cat = Cat(("a", Entity(new List<LvnLayer> { new LvnLayer("/a.png") })));
            Assert.AreEqual(0, cat.Resolve("nope", null).Count);
            Assert.IsFalse(cat.Has("nope"));
            Assert.IsTrue(cat.Has("a"));
        }

        [Test]
        public void Layer_SerialisesStringWhenUnconditional_ObjectWhenConditional()
        {
            Assert.AreEqual("\"/a.png\"", JsonConvert.SerializeObject(new LvnLayer("/a.png")));
            StringAssert.Contains("\"when\":\"x>0\"", JsonConvert.SerializeObject(new LvnLayer("/a.png", "x>0")));
        }

        [Test]
        public void Entity_DeserialisesMixedLayers_StringAndObject()
        {
            var json = @"{
                ""name"": ""Mara"",
                ""layers"": [
                    ""/a/body_{pose}.png"",
                    { ""url"": ""/a/blush.png"", ""when"": ""warmth >= 1"" }
                ],
                ""defaults"": { ""pose"": ""standing"" }
            }";
            var e = JsonConvert.DeserializeObject<LvnSpriteEntity>(json);
            Assert.AreEqual(2, e.layers.Count);
            Assert.AreEqual("/a/body_{pose}.png", e.layers[0].url);
            Assert.IsTrue(string.IsNullOrEmpty(e.layers[0].when));
            Assert.AreEqual("/a/blush.png", e.layers[1].url);
            Assert.AreEqual("warmth >= 1", e.layers[1].when);
        }
    }
}
