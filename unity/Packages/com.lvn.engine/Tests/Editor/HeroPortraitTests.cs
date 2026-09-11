using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЖИВОЙ ПОРТРЕТ (TR-68): лицо в кружке — это ТЕКУЩИЙ облик героя плюс его
    /// ТЕКУЩАЯ эмоция. Портрет, отставший от гардероба, хуже статичной
    /// картинки: игрок видит, что игра про него забыла.
    /// </summary>
    public class HeroPortraitTests
    {
        private static LvnManifest Hero(string entity = "victoria")
        {
            var m = new LvnManifest
            {
                sprites = new Dictionary<string, LvnSpriteEntity>
                {
                    [entity] = new LvnSpriteEntity
                    {
                        layers = new List<LvnLayer>
                        {
                            new LvnLayer("/art/body_{outfit}.png"),
                            new LvnLayer("/art/face_{emotion}.png"),
                        },
                        defaults = new Dictionary<string, string>
                        { { "outfit", "base" }, { "emotion", "calm" } },
                        wardrobe = new Dictionary<string, LvnWardrobeSlot>
                        {
                            ["outfit"] = new LvnWardrobeSlot(),
                            ["emotion"] = new LvnWardrobeSlot(),
                        },
                    },
                },
                ui = new LvnUiConfig { wardrobe = new WardrobeConfig { entity = entity } },
            };
            return m;
        }

        [TearDown]
        public void Reset() => LvnWardrobe.Clear("victoria");

        /// <summary>Надел наряд — лицо взяло его немедленно.</summary>
        [Test]
        public void ThePortraitFollowsTheWornLook()
        {
            var m = Hero();
            var before = LvnHeroPortrait.Layers(m);
            Assert.IsNotNull(before, "у героя с слоями портрет обязан собираться");
            CollectionAssert.Contains(before, "/art/body_base.png");

            LvnWardrobe.Equip("victoria", "outfit", "winter");
            var after = LvnHeroPortrait.Layers(m);
            CollectionAssert.Contains(after, "/art/body_winter.png",
                "портрет остался в прежнем наряде — он отстал от гардероба");
        }

        /// <summary>Эмоция берётся У СЦЕНЫ: лицо в гардеробе не надевается
        /// (TR-72), и надетого значения у этой оси не бывает вовсе.</summary>
        [Test]
        public void TheFaceTakesTheLiveEmotion()
        {
            var m = Hero();
            CollectionAssert.Contains(LvnHeroPortrait.Layers(m, "smile"), "/art/face_smile.png");

            // Сцена молчит — спокойное лицо по умолчанию оси, а не пустой слой.
            CollectionAssert.Contains(LvnHeroPortrait.Layers(m, ""), "/art/face_calm.png");
        }

        /// <summary>Нет героя гардероба — нет и портрета: показать чужое лицо
        /// хуже, чем оставить картинку из набора.</summary>
        [Test]
        public void WithoutAHeroThereIsNoPortrait()
        {
            var m = Hero();
            m.ui.wardrobe.entity = null;
            Assert.IsNull(LvnHeroPortrait.Layers(m));
            Assert.IsNull(LvnHeroPortrait.HeroOf(m));
        }

        /// <summary>Окно портрета: движковое, пока новелла не назвала своё.
        /// Ноль в манифесте — это «не назвала», а не «нулевой зум»: зум ноль
        /// схлопнул бы лицо в точку.</summary>
        [Test]
        public void TheWindowFallsBackToTheEngineNumbers()
        {
            var m = Hero();
            LvnHeroPortrait.Window(m, out float zoom, out float anchor);
            Assert.AreEqual(LvnHeroPortrait.Zoom, zoom, 0.001f);
            Assert.AreEqual(LvnHeroPortrait.Anchor, anchor, 0.001f);

            m.ui.browse = new BrowseConfig { portrait = new PortraitFrame { zoom = 0f, anchor = 0f } };
            LvnHeroPortrait.Window(m, out zoom, out anchor);
            Assert.Greater(zoom, 0.1f, "нулевой зум схлопнул лицо в точку");

            m.ui.browse.portrait = new PortraitFrame { zoom = 4f, anchor = 0.2f };
            LvnHeroPortrait.Window(m, out zoom, out anchor);
            Assert.AreEqual(4f, zoom, 0.001f);
            Assert.AreEqual(0.2f, anchor, 0.001f);
        }
    }
}
