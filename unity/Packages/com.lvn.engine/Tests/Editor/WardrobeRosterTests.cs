using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СТОЛБИК ГАРДЕРОБА ПОКАЗЫВАЕТ СВОИХ (TR-61). Каталог на сервере один на
    /// все новеллы, и «все, у кого есть шкаф» приводило в столбик героиню из
    /// чужой истории — со своими вкладками и своим набором эмоций.
    /// </summary>
    public class WardrobeRosterTests
    {
        private static Dictionary<string, LvnSpriteEntity> Cast()
        {
            LvnSpriteEntity Dressable(string storyVar) => new LvnSpriteEntity
            {
                wardrobe = new Dictionary<string, LvnWardrobeSlot>
                { ["outfit"] = new LvnWardrobeSlot { storyVar = storyVar } },
            };
            return new Dictionary<string, LvnSpriteEntity>
            {
                ["victoria"] = Dressable("look"),
                ["katya"]    = Dressable("katya_look"),
                ["cold_main"] = Dressable("cold_look"),   // героиня ДРУГОЙ новеллы
                ["porch"] = new LvnSpriteEntity(),        // фон: шкафа нет
            };
        }

        /// <summary>В каталоге многих новелл берём героя и тех, кто на сцене —
        /// чужой там взяться не может.</summary>
        [Test]
        public void ACatalogOfManyNovelsShowsOnlyItsOwn()
        {
            var picked = LvnWardrobeRoster.Pick(Cast(), null, "victoria",
                                                new[] { "victoria", "katya" }, titleCount: 4);
            CollectionAssert.AreEqual(new[] { "victoria", "katya" }, picked);
            CollectionAssert.DoesNotContain(picked, "cold_main",
                "в столбик попала героиня чужой новеллы");
        }

        /// <summary>Авторский список — закон: ни сцена, ни каталог его не
        /// дополняют.</summary>
        [Test]
        public void TheAuthoredListWins()
        {
            var picked = LvnWardrobeRoster.Pick(Cast(), new[] { "katya", "victoria" }, "victoria",
                                                new[] { "cold_main" }, titleCount: 4);
            CollectionAssert.AreEqual(new[] { "katya", "victoria" }, picked);
        }

        /// <summary>Каталог одной новеллы — прежнее поведение: чужих в нём нет,
        /// и герой, который сейчас не на сцене, всё равно одевается.</summary>
        [Test]
        public void ASingleNovelCatalogStillShowsEveryone()
        {
            var picked = LvnWardrobeRoster.Pick(Cast(), null, "victoria", null, titleCount: 1);
            Assert.Greater(picked.Count, 1, "одиночная новелла потеряла своих героев");
        }

        /// <summary>Один герой под тремя именами — одна таблетка: у импортов
        /// это Mira / demo_main / Главный_герой, и признак «тот же» — набор
        /// сюжетных переменных шкафа.</summary>
        [Test]
        public void AliasesOfOneCharacterCollapse()
        {
            var cast = Cast();
            cast["demo_main"] = new LvnSpriteEntity
            {
                wardrobe = new Dictionary<string, LvnWardrobeSlot>
                { ["outfit"] = new LvnWardrobeSlot { storyVar = "look" } },   // та же переменная
            };
            var picked = LvnWardrobeRoster.Pick(cast, null, "victoria",
                                                new[] { "demo_main" }, titleCount: 4);
            CollectionAssert.AreEqual(new[] { "victoria" }, picked);
        }
    }
}
