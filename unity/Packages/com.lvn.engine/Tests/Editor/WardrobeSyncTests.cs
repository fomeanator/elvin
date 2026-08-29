using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Связной гардероба: перекладывает только НАЗВАННОЕ и НЕПУСТОЕ.
    /// Потерянное согласие видно игроку вспышкой случайного наряда при открытии
    /// листа.</summary>
    public class WardrobeSyncTests
    {
        private const string Entity = "test_sync_hero";

        private static Dictionary<string, LvnWardrobeSlot> Catalog() => new Dictionary<string, LvnWardrobeSlot>
        {
            ["clothes"] = new LvnWardrobeSlot { storyVar = "Wardrobe.clothes" },
            ["hair"] = new LvnWardrobeSlot { storyVar = "Wardrobe.hair" },
            ["ring"] = new LvnWardrobeSlot(),          // ось без storyVar — истории неизвестна
        };

        [SetUp]
        [TearDown]
        public void Clean() => LvnWardrobe.Clear(Entity);

        // ── надетое → переменные ──

        [Test]
        public void НадетоеЛожитсяВНазванныеПеременные()
        {
            LvnWardrobe.Equip(Entity, "clothes", "coat");
            LvnWardrobe.Equip(Entity, "hair", "bob");

            var vars = new Dictionary<string, string>();
            LvnWardrobeSync.ToVars(Entity, Catalog(), (k, v) => vars[k] = v);

            Assert.AreEqual("coat", vars["Wardrobe.clothes"]);
            Assert.AreEqual("bob", vars["Wardrobe.hair"]);
        }

        [Test]
        public void ОсьБезИмениПеременнойНеПерекладывается()
        {
            LvnWardrobe.Equip(Entity, "ring", "gold");

            var vars = new Dictionary<string, string>();
            LvnWardrobeSync.ToVars(Entity, Catalog(), (k, v) => vars[k] = v);

            Assert.AreEqual(0, vars.Count, "перекладывается только НАЗВАННОЕ");
        }

        [Test]
        public void ПустойГардеробНичегоНеПишет()
        {
            var vars = new Dictionary<string, string>();
            LvnWardrobeSync.ToVars(Entity, Catalog(), (k, v) => vars[k] = v);
            Assert.AreEqual(0, vars.Count, "ничего не надето — нечего и класть");
        }

        [Test]
        public void НеснятаяОсьНеЗатираетПеременнуюПустотой()
        {
            LvnWardrobe.Equip(Entity, "clothes", "coat");

            var vars = new Dictionary<string, string> { ["Wardrobe.hair"] = "уже_стояло" };
            LvnWardrobeSync.ToVars(Entity, Catalog(), (k, v) => vars[k] = v);

            Assert.AreEqual("уже_стояло", vars["Wardrobe.hair"],
                "пустая ось не должна стирать то, что глава поставила сама");
        }

        [Test]
        public void ToVarsБезВходаНеБросает()
        {
            Assert.DoesNotThrow(() => LvnWardrobeSync.ToVars(null, Catalog(), (k, v) => { }));
            Assert.DoesNotThrow(() => LvnWardrobeSync.ToVars("", Catalog(), (k, v) => { }));
            Assert.DoesNotThrow(() => LvnWardrobeSync.ToVars(Entity, null, (k, v) => { }));
            Assert.DoesNotThrow(() => LvnWardrobeSync.ToVars(Entity, Catalog(), null));
        }

        // ── переменные → надетое ──

        [Test]
        public void ПеременныеСтановятсяНадетым()
        {
            var vars = new Dictionary<string, string>
                { ["Wardrobe.clothes"] = "gown", ["Wardrobe.hair"] = "long" };
            LvnWardrobeSync.FromVars(Entity, Catalog(), k => vars.TryGetValue(k, out var v) ? v : null);

            Assert.AreEqual("gown", LvnWardrobe.Equipped(Entity)["clothes"]);
            Assert.AreEqual("long", LvnWardrobe.Equipped(Entity)["hair"]);
        }

        [Test]
        public void ПустаяПеременнаяНеРаздеваетГероиню()
        {
            // Иначе глава, не поставившая переменную, снимала бы наряд игрока.
            LvnWardrobe.Equip(Entity, "clothes", "coat");
            LvnWardrobeSync.FromVars(Entity, Catalog(), k => "");

            Assert.AreEqual("coat", LvnWardrobe.Equipped(Entity)["clothes"]);
        }

        [Test]
        public void ОтсутствующаяПеременнаяНеРаздеваетГероиню()
        {
            LvnWardrobe.Equip(Entity, "clothes", "coat");
            LvnWardrobeSync.FromVars(Entity, Catalog(), k => null);

            Assert.AreEqual("coat", LvnWardrobe.Equipped(Entity)["clothes"]);
        }

        [Test]
        public void FromVarsБезВходаНеБросает()
        {
            Assert.DoesNotThrow(() => LvnWardrobeSync.FromVars(null, Catalog(), k => "x"));
            Assert.DoesNotThrow(() => LvnWardrobeSync.FromVars(Entity, null, k => "x"));
            Assert.DoesNotThrow(() => LvnWardrobeSync.FromVars(Entity, Catalog(), null));
        }

        [Test]
        public void ТудаИОбратноДаётТоЖеСамое()
        {
            LvnWardrobe.Equip(Entity, "clothes", "coat");
            LvnWardrobe.Equip(Entity, "hair", "bob");

            var vars = new Dictionary<string, string>();
            LvnWardrobeSync.ToVars(Entity, Catalog(), (k, v) => vars[k] = v);
            LvnWardrobe.Clear(Entity);
            LvnWardrobeSync.FromVars(Entity, Catalog(), k => vars.TryGetValue(k, out var v) ? v : null);

            Assert.AreEqual("coat", LvnWardrobe.Equipped(Entity)["clothes"]);
            Assert.AreEqual("bob", LvnWardrobe.Equipped(Entity)["hair"]);
        }
    }
}
