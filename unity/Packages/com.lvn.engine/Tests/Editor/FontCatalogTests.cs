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
        public void OpticalSizeIsCorrectedPerFamily()
        {
            // Один кегль у разных гарнитур выглядит разной величиной: у
            // рукописной строчные вдвое ниже, у пиксельной буквы тяжелее.
            LvnPrefs.FontFamily = "";
            Assert.AreEqual(30, LvnFonts.Size(30), "без выбора игрока авторский кегль не трогаем");

            LvnPrefs.FontFamily = "caveat";
            Assert.Greater(LvnFonts.Size(30), 30, "рукописную поднимаем, иначе «Крупный» читается как «Обычный»");

            LvnPrefs.FontFamily = "pixel";
            Assert.Less(LvnFonts.Size(30), 30, "пиксельную опускаем, иначе подписи не влезают в кнопки");
        }

        [Test]
        public void ScaleSpreadShrinksButKeepsOrder()
        {
            // «Слишком большой разброс у маленьких и больших текстов»: у
            // характерных гарнитур крупное вылезает сильнее задуманного, и
            // заголовок рядом с подписью выглядит плакатом.
            LvnPrefs.FontFamily = "";
            int plainSmall = LvnFonts.Size(20), plainBig = LvnFonts.Size(64);

            LvnPrefs.FontFamily = "pixel";
            int small = LvnFonts.Size(20), big = LvnFonts.Size(64);

            Assert.Less(small, big, "порядок ступеней обязан сохраниться");
            Assert.Less(big / (float)small, plainBig / (float)plainSmall,
                "разброс у характерной гарнитуры обязан быть МЕНЬШЕ авторского");
        }

        [Test]
        public void OneBadCatalogRowCannotBreakEveryScreen()
        {
            // Числа гарнитур подбирают глазом, и однажды подберут неудачно.
            foreach (var f in LvnFonts.Families)
            {
                LvnPrefs.FontFamily = f.Id;
                foreach (int b in new[] { 20, 30, 48, 64 })
                {
                    Assert.GreaterOrEqual(LvnFonts.Size(b), Mathf.RoundToInt(b * 0.6f) - 1, f.Title);
                    Assert.LessOrEqual(LvnFonts.Size(b), Mathf.RoundToInt(b * 1.75f) + 1, f.Title);
                }
            }
        }

        [Test]
        public void SizeNeverCollapsesToZero()
        {
            LvnPrefs.FontFamily = "pixel";
            Assert.GreaterOrEqual(LvnFonts.Size(1), 1, "кегль ноль — это невидимый текст");
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
