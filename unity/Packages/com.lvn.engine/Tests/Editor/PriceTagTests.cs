using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Ценник: облик денег приходит из манифеста, движок знает только
    /// ФОРМУ — не слова. Одна сумма выглядела по-разному в двух местах одного
    /// экрана: «◆ 1 200» на карточке и «Купить: 1 200 золота» на кнопке под ней.</summary>
    public class PriceTagTests
    {
        [SetUp]
        [TearDown]
        public void Clean()
        {
            LvnPriceTag.Learn(null);
            LvnWords.Learn(null);
            LvnWords.Translate(null);
        }

        private static void LearnGems() => LvnPriceTag.Learn(new Dictionary<string, CurrencyLook>
        {
            ["crystals"] = new CurrencyLook { name = "Кристаллы", unit = "кристаллов", icon = "Gem", color = "#f0c860" },
        });

        [Test]
        public void БезМанифестаНазваниеЭтоСамИдентификатор()
        {
            // Движок не придумывает слов за автора.
            Assert.AreEqual("crystals", LvnPriceTag.Of("crystals").Name);
        }

        [Test]
        public void ПустаяВалютаНеРонаетЦенник()
        {
            Assert.AreEqual(string.Empty, LvnPriceTag.Of(null).Name);
            Assert.AreEqual(string.Empty, LvnPriceTag.Of("").Name);
            Assert.AreEqual("5", LvnPriceTag.Full(null, 5),
                "врать про валюту хуже, чем промолчать: остаётся голое число");
        }

        [Test]
        public void ОбликБерётсяИзМанифеста()
        {
            LearnGems();
            var look = LvnPriceTag.Of("crystals");
            Assert.AreEqual("Кристаллы", look.Name);
            Assert.AreEqual("кристаллов", look.Unit);
            Assert.AreEqual(LvnIcon.Gem, look.Icon);
        }

        [Test]
        public void ИдентификаторВалютыБезРазницыВРегистре()
        {
            LearnGems();
            Assert.AreEqual("Кристаллы", LvnPriceTag.Of("CRYSTALS").Name,
                "манифест и скрипт пишут валюту по-разному — деньги от этого не раздваиваются");
        }

        [Test]
        public void ФормаПриСуммеСильнееНазвания()
        {
            LearnGems();
            Assert.AreEqual("1,200 кристаллов", LvnPriceTag.Full("crystals", 1200));
        }

        [Test]
        public void БезФормыБерётсяНазвание()
        {
            LvnPriceTag.Learn(new Dictionary<string, CurrencyLook>
                { ["gold"] = new CurrencyLook { name = "Золото" } });
            Assert.AreEqual("7 Золото", LvnPriceTag.Full("gold", 7));
        }

        [Test]
        public void СуммаПишетсяРазрядамиПоЯзыкуНовеллы()
        {
            // Разделитель — из языка новеллы, а не из настроек телефона.
            Assert.AreEqual("1,200", LvnPriceTag.Amount(1200));
            LvnWords.Learn(new Dictionary<string, string> { ["unit.group"] = " " });
            Assert.AreEqual("1 200", LvnPriceTag.Amount(1200));
        }

        [Test]
        public void НовыйМанифестСтираетПрежниеОблики()
        {
            LearnGems();
            LvnPriceTag.Learn(null);
            Assert.AreEqual("crystals", LvnPriceTag.Of("crystals").Name,
                "смена новеллы не должна оставлять чужие слова про деньги");
        }

        [Test]
        public void БитыеЗаписиМанифестаПропускаются()
        {
            Assert.DoesNotThrow(() => LvnPriceTag.Learn(new Dictionary<string, CurrencyLook>
            {
                [""] = new CurrencyLook { name = "Ничьё" },
                ["gold"] = null,
            }));
            Assert.AreEqual("gold", LvnPriceTag.Of("gold").Name);
        }

        [Test]
        public void НезнакомоеИмяЗначкаНеСтираетЗначок()
        {
            LvnPriceTag.Learn(new Dictionary<string, CurrencyLook>
                { ["energy"] = new CurrencyLook { icon = "такогоЗначкаНет" } });
            Assert.AreEqual(LvnIcon.Energy, LvnPriceTag.Of("energy").Icon,
                "опечатка в имени значка откатывается к догадке по смыслу, а не к пустоте");
        }

        [Test]
        public void ДогадкаОЗначкеОднаСИконками()
        {
            // Умолчания двух домов не совпадали: валюта «золото» без настройки
            // получала камень в магазине и монету в строке состояния.
            foreach (var c in new[] { "gold", "coins", "золото", "энергия", "crystals", "выдуманное" })
                Assert.AreEqual(LvnIcons.ForCurrency(c), LvnPriceTag.Of(c).Icon, c);
        }

        [Test]
        public void КривойЦветНеРонаетЦенник()
        {
            LvnPriceTag.Learn(new Dictionary<string, CurrencyLook>
                { ["gold"] = new CurrencyLook { color = "не цвет" } });
            Assert.AreEqual(LvnIcons.CurrencyColor("gold"), LvnPriceTag.Of("gold").Tint,
                "мусор в поле цвета откатывается к умолчанию валюты");
        }

        [Test]
        public void РядЭтоЗначокИСумма()
        {
            var row = LvnPriceTag.Tag("crystals", 1200);
            Assert.AreEqual(2, row.childCount, "значок + сумма — собирает ДОМ, а не экран");
        }

        [Test]
        public void ЗначокМожноПоставитьСправа()
        {
            var left = LvnPriceTag.Tag("crystals", 5, new LvnPriceTag.Row { IconFirst = true });
            var right = LvnPriceTag.Tag("crystals", 5, new LvnPriceTag.Row { IconFirst = false });

            Assert.IsInstanceOf<UnityEngine.UIElements.Label>(left[1], "слева значок — сумма второй");
            Assert.IsInstanceOf<UnityEngine.UIElements.Label>(right[0], "справа значок — сумма первой");
        }
    }
}
