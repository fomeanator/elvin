using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОДПИСЬ, ЗАДАННАЯ ПОЛЕМ СЕКЦИИ, доходит до экрана.
    ///
    /// <para>В живом манифесте стояли «Надеть», «Надето», «Снять» и
    /// «+{0} бонусом», а игрок видел Equip, Equipped, None и «+300 bonus»:
    /// экран, не знающий про поле, звал словарь напрямую. Поле было написано
    /// правильно — его просто никто не спрашивал.</para>
    /// </summary>
    public class AuthoredWordsTests
    {
        [TearDown]
        public void Убрать() => LvnWords.Learn(null, null, null);

        private static LvnUiConfig Ui() => new LvnUiConfig
        {
            wardrobe = new WardrobeConfig { remove_text = "Снять" },
            store = new StoreConfig { bonus_text = "+{0} бонусом" },
        };

        [Test]
        public void ИмяПунктаМенюСтановитсяОбычнымСловом()
        {
            // Поле menu_label читалось тремя способами, и у настроек — мимо
            // словаря вовсе: пункт не переводился ничем.
            var ui = new LvnUiConfig
            {
                store = new StoreConfig { menu_label = "Лавка" },
                wardrobe = new WardrobeConfig { menu_label = "Наряды" },
                settings = new SettingsConfig { menu_label = "Настройки" },
            };
            LvnWords.Learn(null, null, ui);
            Assert.AreEqual("Лавка", LvnWords.Of("menu.store", "Store"));
            Assert.AreEqual("Наряды", LvnWords.Of("menu.wardrobe", "Wardrobe"),
                "то же имя видит и кнопка верхней панели — ключ один");
            Assert.AreEqual("Настройки", LvnWords.Of("menu.settings", "Settings"));
        }

        [Test]
        public void ПолеСекцииСтановитсяКлючомСловаря()
        {
            var map = LvnAuthoredWords.Fold(Ui());
            Assert.AreEqual("Снять", map["wardrobe.none"], "снятие — тот же выбор, что надеть");
            Assert.AreEqual("+{0} бонусом", map["shop.bonus"]);
        }

        [Test]
        public void ПустоеПолеНеЗаводитКлюча()
        {
            var ui = new LvnUiConfig { wardrobe = new WardrobeConfig { remove_text = "" } };
            CollectionAssert.IsEmpty(LvnAuthoredWords.Fold(ui), "пустая строка — не слово");
            CollectionAssert.IsEmpty(LvnAuthoredWords.Fold(null), "нет облика — нет и слов");
        }

        [Test]
        public void ЭкранПолучаетАвторскоеСловоЧерезОбычныйВызов()
        {
            LvnWords.Learn(null, null, Ui());
            Assert.AreEqual("Снять", LvnWords.Of("wardrobe.none", "None"),
                "экран зовёт словарь как всегда — поле уже влито");
            Assert.AreEqual("+300 бонусом", LvnWords.Of("shop.bonus", "+{0} bonus", 300),
                "шаблон с числом работает и у авторского слова");
        }

        [Test]
        public void СловарьСильнееПоля()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["wardrobe.none"] = "Ничего" }, null, Ui());
            Assert.AreEqual("Ничего", LvnWords.Of("wardrobe.none", "None"),
                "автор, написавший слово дважды, имел в виду то, что ближе к словарю");
        }

        [Test]
        public void БезПоляОстаётсяУмолчаниеВызывающего()
        {
            LvnWords.Learn(null, null, new LvnUiConfig());
            Assert.AreEqual("None", LvnWords.Of("wardrobe.none", "None"));
        }
    }
}
