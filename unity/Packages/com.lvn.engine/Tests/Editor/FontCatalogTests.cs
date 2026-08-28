using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>Каталог гарнитур: настройка, которую нельзя проверить взглядом,
    /// ощущается сломанной — значит каждый вариант обязан существовать и
    /// отличаться от соседей.</summary>
    public class FontCatalogTests
    {
        private string _saved;

        [SetUp]
        public void Save() => _saved = LvnPrefs.FontFamily;

        [TearDown]
        public void Restore() => LvnPrefs.FontFamily = _saved;

        [Test]
        public void EveryFamilyLoadsFromResources()
        {
            foreach (var f in LvnFonts.Families)
            {
                Assert.IsNotNull(Resources.Load<Font>(f.Path), $"нет файла шрифта: {f.Title} ({f.Path})");
                Assert.IsNotNull(Resources.Load<Font>(f.Display), $"нет заголовочного: {f.Title} ({f.Display})");
            }
        }

        [Test]
        public void KeysAreUniqueAndPathsDiffer()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            var paths = new System.Collections.Generic.HashSet<string>();
            foreach (var f in LvnFonts.Families)
            {
                Assert.IsTrue(ids.Add(f.Id), $"ключ повторяется: {f.Id}");
                Assert.IsTrue(paths.Add(f.Path), $"две гарнитуры смотрят в один файл: {f.Path}");
            }
            Assert.GreaterOrEqual(LvnFonts.Families.Length, 5);
        }

        [Test]
        public void PlayerChoiceWinsAndCanBeReturned()
        {
            LvnPrefs.FontFamily = "";
            Assert.IsFalse(LvnFonts.PlayerPicked, "пусто — гарнитуру выбирает новелла");

            LvnPrefs.FontFamily = "caveat";
            Assert.IsTrue(LvnFonts.PlayerPicked);
            Assert.AreEqual("caveat", LvnFonts.Chosen.Id);

            LvnPrefs.FontFamily = "";
            Assert.IsFalse(LvnFonts.PlayerPicked, "«как в игре» обязано возвращать тему, а не прошлый выбор");
            Assert.AreEqual(LvnFonts.Families[0].Id, LvnFonts.Chosen.Id);
        }

        [Test]
        public void UnknownKeyFallsBackInsteadOfLeavingNoFont()
        {
            LvnPrefs.FontFamily = "нет-такой";
            Assert.AreEqual(LvnFonts.Families[0].Id, LvnFonts.FamilyOf(LvnPrefs.FontFamily).Id,
                "неизвестный ключ (старая настройка, чужая сборка) не имеет права оставить текст без шрифта");
        }
    }
}
