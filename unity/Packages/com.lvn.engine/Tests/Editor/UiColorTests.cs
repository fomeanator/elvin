using System.Text.RegularExpressions;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>Цвет из строки — один дом на весь движок. Пять реализаций одного
    /// понятия неизбежно расходятся; здесь проверяется то, чем они расходились:
    /// решётка, имена, токены темы и ЖАЛОБА на мусор.</summary>
    public class UiColorTests
    {
        [Test]
        public void РешёткаНеобязательна()
        {
            // Автор пишет «ff0000» без решётки — и это тот же красный.
            Assert.AreEqual(Color.red, UiColor.Parse("ff0000", Color.black));
            Assert.AreEqual(Color.red, UiColor.Parse("#ff0000", Color.black));
        }

        [Test]
        public void ИменаЦветовUnityТожеЦвета()
        {
            Assert.AreEqual(Color.red, UiColor.Parse("red", Color.black));
        }

        [Test]
        public void ПолучилосьИНеПолучилосьРазныеСобытия()
        {
            // По одному результату они неразличимы: «цвет вышел таким же, как
            // умолчание» и «цвет не вышел» — разные вещи для вызывающего.
            Assert.IsTrue(UiColor.TryParse("#000000", out var black));
            Assert.AreEqual(Color.black, black);

            Assert.IsFalse(UiColor.TryParse("не цвет", out _));
            Assert.IsFalse(UiColor.TryParse("", out _));
            Assert.IsFalse(UiColor.TryParse(null, out _));
        }

        [Test]
        public void ПрозрачностьЧитаетсяВосьмойЦифрой()
        {
            Assert.IsTrue(UiColor.TryParse("#00ff0080", out var c));
            Assert.AreEqual(0.5f, c.a, 0.01f);
        }

        [Test]
        public void ТокенТемыЭтоНеHex()
        {
            // Parse про «шестнадцать цифр», Token — про тему. Первое второго не знает.
            Assert.AreEqual(LvnTokens.Accent, UiColor.Token("accent", Color.black));
            Assert.AreEqual(LvnTokens.Gold, UiColor.Token("gold", Color.black));
            Assert.AreEqual(new Color(0, 0, 0, 0), UiColor.Token("clear", Color.white));
        }

        [Test]
        public void ТокенПонимаетИHex()
        {
            Assert.AreEqual(Color.red, UiColor.Token("#ff0000", Color.black));
        }

        [Test]
        public void ПустоеИмяТокенаМолчаБерётУмолчание()
        {
            Assert.AreEqual(Color.black, UiColor.Token(null, Color.black));
            Assert.AreEqual(Color.black, UiColor.Token("", Color.black));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void НеизвестныйТокенЖалуется()
        {
            // Опечатка иначе молча даёт прозрачный цвет, и «нарисовалось не то»
            // приходится искать глазами.
            LogAssert.Expect(LogType.Warning, new Regex("lvn-ui"));
            Assert.AreEqual(Color.black, UiColor.Token("акцентт", Color.black));
        }

        [Test]
        public void ПодстановкаПеременнойНеЖалоба()
        {
            // «{color}» разрешится позже — жаловаться на неё рано.
            Assert.AreEqual(Color.black, UiColor.Token("{skin.accent}", Color.black));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ПолеКомандыБезЦветаОставляетПрежний()
        {
            var cmd = new JObject { ["op"] = "dim" };
            Assert.AreEqual(Color.green, UiColor.FromCmd(cmd, "color", Color.green));
            Assert.AreEqual(Color.green, UiColor.FromCmd(null, "color", Color.green),
                "команды может не быть вовсе — это не мусор, а отсутствие");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void МусорВПолеКомандыЖалуетсяИНеМеняетЦвет()
        {
            // Строку писал автор, а опечатка в цвете иначе выглядит как
            // «эффект не сработал».
            LogAssert.Expect(LogType.Warning, new Regex("lvn-ui"));
            var cmd = new JObject { ["color"] = "зелёненький" };
            Assert.AreEqual(Color.green, UiColor.FromCmd(cmd, "color", Color.green));
        }

        [Test]
        public void ЦветИзПоляКомандыБерётсяБезРешётки()
        {
            var cmd = new JObject { ["color"] = "ff0000" };
            Assert.AreEqual(Color.red, UiColor.FromCmd(cmd, "color", Color.black));
        }
    }
}
