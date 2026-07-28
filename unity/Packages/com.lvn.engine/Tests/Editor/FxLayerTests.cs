using System.Reflection;
using Lvn;
using Lvn.UI.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Lvn.Tests
{
    public class FxLayerTests
    {
        static T Private<T>(object target, string field)
        {
            var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(info, $"missing private field {field}");
            return (T)info.GetValue(target);
        }

        [Test]
        public void FullFrameFxAcceptsAtmosphereCombatAndStyleFields()
        {
            var go = new GameObject("fx-test");
            try
            {
                var fx = go.AddComponent<LvnFxStack>();
                fx.Apply(JObject.Parse(@"{
                    'fog':0.4,'rain':0.5,'snow':0.6,'embers':0.7,
                    'blood':0.8,'poison':0.3,'shockwave':1,
                    'speedlines':0.9,'dream':0.2,'sepia':0.25,
                    'posterize':0.35,'letterbox':0.45,
                    'shock_x':0.2,'shock_y':0.7,
                    'space':0.85,'space_x':0.4,'space_y':0.35,
                    'space_radius':0.22,'space_color':'#8b42ff'
                }"));

                Assert.AreEqual(0.4f, Private<float>(fx, "_tFog"), 0.0001f);
                Assert.AreEqual(0.7f, Private<float>(fx, "_tEmbers"), 0.0001f);
                Assert.AreEqual(0.8f, Private<float>(fx, "_tBlood"), 0.0001f);
                Assert.AreEqual(1f, Private<float>(fx, "_tShockwave"), 0.0001f);
                Assert.AreEqual(0.9f, Private<float>(fx, "_tSpeedlines"), 0.0001f);
                Assert.AreEqual(0.2f, Private<float>(fx, "_tDream"), 0.0001f);
                Assert.AreEqual(0.45f, Private<float>(fx, "_tLetterbox"), 0.0001f);
                Assert.AreEqual(new Vector2(0.2f, 0.7f), Private<Vector2>(fx, "_fxCenter"));
                Assert.AreEqual(0.85f, Private<float>(fx, "_tSpace"), 0.0001f);
                Assert.AreEqual(new Vector2(0.4f, 0.35f), Private<Vector2>(fx, "_spaceCenter"));
                Assert.AreEqual(0.22f, Private<float>(fx, "_spaceRadius"), 0.0001f);

                fx.Apply(JObject.Parse(@"{'off':true}"));
                Assert.AreEqual(0f, Private<float>(fx, "_tFog"));
                Assert.AreEqual(0f, Private<float>(fx, "_tBlood"));
                Assert.AreEqual(0f, Private<float>(fx, "_tLetterbox"));
                Assert.AreEqual(0f, Private<float>(fx, "_tSpace"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SpriteFxAcceptsTransformationAndMotionFields()
        {
            var go = new GameObject("sfx-test");
            try
            {
                LvnSpriteFxDriver.Apply(go, JObject.Parse(@"{
                    'ghost':0.8,'petrify':0.7,'hologram':0.6,
                    'burn':0.5,'rim':0.4,'shake':0.3,
                    'aura':0.9,'aura_style':'fire',
                    'blade':0.8,'lightning':0.6,'runes':0.5
                }"));
                var fx = go.GetComponent<LvnSpriteFxDriver>();
                Assert.NotNull(fx);
                Assert.AreEqual(0.8f, Private<float>(fx, "_tGhost"), 0.0001f);
                Assert.AreEqual(0.7f, Private<float>(fx, "_tPetrify"), 0.0001f);
                Assert.AreEqual(0.6f, Private<float>(fx, "_tHologram"), 0.0001f);
                Assert.AreEqual(0.5f, Private<float>(fx, "_tBurn"), 0.0001f);
                Assert.AreEqual(0.4f, Private<float>(fx, "_tRim"), 0.0001f);
                Assert.AreEqual(0.3f, Private<float>(fx, "_tShake"), 0.0001f);
                Assert.AreEqual(0.9f, Private<float>(fx, "_tAura"), 0.0001f);
                Assert.AreEqual(2f, Private<float>(fx, "_auraStyle"), 0.0001f);
                Assert.Greater(Private<Color>(fx, "_auraColor").r, 0.9f);
                Assert.AreEqual(0.8f, Private<float>(fx, "_tBlade"), 0.0001f);
                Assert.AreEqual(0.6f, Private<float>(fx, "_tLightning"), 0.0001f);
                Assert.AreEqual(0.5f, Private<float>(fx, "_tRunes"), 0.0001f);

                LvnSpriteFxDriver.Apply(go, JObject.Parse(@"{'off':true}"));
                Assert.AreEqual(0f, Private<float>(fx, "_tGhost"));
                Assert.AreEqual(0f, Private<float>(fx, "_tShake"));
                Assert.AreEqual(0f, Private<float>(fx, "_tAura"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SpriteFxCanTargetAndResetACompositePart()
        {
            var actor = new GameObject("hero");
            try
            {
                var weapon = new GameObject("layer:weapon",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                weapon.transform.SetParent(actor.transform, false);

                LvnSpriteFxDriver.Apply(actor, JObject.Parse(@"{
                    'part':'weapon','blade':1,'blade_color':'#c8f5ff','runes':0.4
                }"));

                Assert.IsNull(actor.GetComponent<LvnSpriteFxDriver>());
                var fx = weapon.GetComponent<LvnSpriteFxDriver>();
                Assert.NotNull(fx);
                Assert.IsTrue(Private<bool>(fx, "_scopedPart"));
                Assert.AreEqual(1f, Private<float>(fx, "_tBlade"), 0.0001f);
                Assert.AreEqual(0.4f, Private<float>(fx, "_tRunes"), 0.0001f);

                LvnSpriteFxDriver.Apply(actor, JObject.Parse(@"{'off':true}"));
                Assert.AreEqual(0f, Private<float>(fx, "_tBlade"));
                Assert.AreEqual(0f, Private<float>(fx, "_tRunes"));
            }
            finally
            {
                Object.DestroyImmediate(actor);
            }
        }

        [Test]
        public void RootAuraBuildsOneStencilUnionAcrossCompositeLayers()
        {
            var actor = new GameObject("composite-hero");
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var layer = new GameObject("layer:" + i,
                        typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    layer.transform.SetParent(actor.transform, false);
                }

                LvnSpriteFxDriver.Apply(actor,
                    JObject.Parse(@"{'aura':1,'aura_style':'distortion'}"));

                var fx = actor.GetComponent<LvnSpriteFxDriver>();
                Assert.NotNull(fx);
                Assert.IsTrue(Private<bool>(fx, "_compositeHalo"));
                Assert.AreEqual(2, Private<System.Collections.IList>(fx, "_maskLayers").Count);
                Assert.AreEqual(2, Private<System.Collections.IList>(fx, "_haloLayers").Count);

                var main = Private<Material>(fx, "_mat");
                var mask = Private<Material>(fx, "_maskMat");
                var halo = Private<Material>(fx, "_haloMat");
                Assert.AreEqual(1f, main.GetFloat("_CompositeSource"), 0.0001f);
                Assert.AreEqual(1f, mask.GetFloat("_StencilOnly"), 0.0001f);
                Assert.Greater(mask.GetFloat("_CompositeDilate"), 0f);
                Assert.AreEqual(1f, halo.GetFloat("_CompositeOnly"), 0.0001f);
                Assert.AreEqual((float)CompareFunction.NotEqual,
                    halo.GetFloat("_StencilComp"), 0.0001f);
                Assert.AreEqual(0f, main.GetFloat("_Aura"), 0.0001f);
                Assert.AreEqual(1f, halo.GetFloat("_Aura"), 0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(actor);
            }
        }

        [TestCase("space", 7f)]
        [TestCase("distortion", 8f)]
        public void SpriteFxAcceptsSpatialAuraStyles(string style, float expected)
        {
            var go = new GameObject("spatial-aura-test");
            try
            {
                LvnSpriteFxDriver.Apply(go,
                    JObject.Parse($"{{'aura':1,'aura_style':'{style}'}}"));
                var fx = go.GetComponent<LvnSpriteFxDriver>();
                Assert.NotNull(fx);
                Assert.AreEqual(expected, Private<float>(fx, "_auraStyle"), 0.0001f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TypewriterClockGlobalCpsOverridesDefault()
        {
            TypewriterClock.GlobalCps = 0f;
            float defaultProgress = TypewriterClock.Progress(1f, 30f);
            Assert.AreEqual(30f, defaultProgress, 0.001f);

            TypewriterClock.GlobalCps = 60f;
            float globalProgress = TypewriterClock.Progress(1f, 30f);
            Assert.AreEqual(60f, globalProgress, 0.001f);

            TypewriterClock.GlobalCps = 0f;
        }

        [Test]
        public void TypewriterClockProgressUsesDefaultWhenGlobalZero()
        {
            TypewriterClock.GlobalCps = 0f;
            float progress = TypewriterClock.Progress(2f, 20f);
            Assert.AreEqual(40f, progress, 0.001f);
        }

        [Test]
        public void TypewriterClockDoneAtCalculatesCorrectly()
        {
            float done = TypewriterClock.DoneAt(10, 3f);
            Assert.AreEqual(12f, done, 0.001f);

            float doneNoFade = TypewriterClock.DoneAt(10, 0f);
            Assert.AreEqual(9f, doneNoFade, 0.001f);
        }

        [Test]
        public void TypewriterClockMinCpsGuardsAgainstZero()
        {
            TypewriterClock.GlobalCps = 0f;
            float progress = TypewriterClock.Progress(1f, 0f);
            Assert.AreEqual(TypewriterClock.MinCps, progress, 0.001f);
        }
    }
}
