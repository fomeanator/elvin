using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧТЕЦ «ДА-НЕТ» — один словарь согласия на весь движок.
    ///
    /// <para>Проверяется ровно то, из-за чего роль и заводилась: авторская
    /// форма, которая работает у актёра, обязана работать и у музыки, и у
    /// переменной, и в кадре. Раньше шесть чтецов отвечали по-разному, и
    /// разница молчала — ни ошибки, ни предупреждения, просто не то поведение.</para>
    /// </summary>
    public class BoolReadingTests
    {
        [TestCase("true"), TestCase("1"), TestCase("yes"), TestCase("y"),
         TestCase("on"), TestCase("да"), TestCase(" YES "), TestCase("Да")]
        public void ФормыСогласия(string written)
            => Assert.IsTrue(LvnBool.Of((JToken)written, false), $"«{written}» — это да");

        [TestCase("false"), TestCase("0"), TestCase("no"), TestCase("n"),
         TestCase("off"), TestCase("нет"), TestCase(" NO "), TestCase("Нет")]
        public void ФормыОтказа(string written)
            => Assert.IsFalse(LvnBool.Of((JToken)written, true), $"«{written}» — это нет");

        [Test]
        public void НастоящиеБулевыИЧисла()
        {
            Assert.IsTrue(LvnBool.Of((JToken)true, false));
            Assert.IsFalse(LvnBool.Of((JToken)false, true));
            Assert.IsTrue(LvnBool.Of((JToken)1, false), "число — это истина, если не ноль");
            Assert.IsFalse(LvnBool.Of((JToken)0, true));
            Assert.IsTrue(LvnBool.Of((JToken)0.5, false));
        }

        [Test]
        public void НепонятоеЗначение_ЭтоНеОтказ_АУмолчание()
        {
            Assert.IsNull(LvnBool.Parse((JToken)"вроде бы"), "не понял — это не «нет»");
            Assert.IsTrue(LvnBool.Of((JToken)"вроде бы", true), "поле берёт своё умолчание");
            Assert.IsFalse(LvnBool.Of((JToken)"вроде бы", false));
        }

        [Test]
        public void ОтсутствиеПоля_ТожеУмолчание()
        {
            var cmd = new JObject();
            Assert.IsTrue(LvnBool.Of(cmd["show"], true));
            Assert.IsFalse(LvnBool.Of(cmd["show"], false));
            Assert.IsNull(LvnBool.Parse(JValue.CreateNull()), "явный null — тоже «не сказано»");
        }

        [Test]
        public void СогласиеИОтказ_ЭтоДваРазныхВопроса()
        {
            // У полей с умолчанием «да» (wait) отличить «написано нет» от
            // «не написано ничего» обязательно — иначе умолчание не выживает.
            var nothing = new JObject()["wait"];
            Assert.IsFalse(LvnBool.On(nothing), "молчание — не согласие");
            Assert.IsFalse(LvnBool.Off(nothing), "и не отказ");

            Assert.IsTrue(LvnBool.Off((JToken)"no"));
            Assert.IsFalse(LvnBool.On((JToken)"no"));
            Assert.IsTrue(LvnBool.On((JToken)"да"));
        }

        [Test]
        public void ПустаяСтрока_НичегоНеСказано()
        {
            Assert.IsNull(LvnBool.Parse(""));
            Assert.IsNull(LvnBool.Parse("   "), "пробелы — тоже молчание");
            Assert.IsTrue(LvnBool.Of("", true));
        }

        [Test]
        public void ЧтениеПоляИИстинностьВыражения_ЭтоРазныеВопросы()
        {
            // «no» как значение поля — отказ; «no» как строка в выражении —
            // непустая строка, то есть истина. Правила НЕ сводятся: второе —
            // семантика языка, общая с JS-рантаймом.
            Assert.IsFalse(LvnBool.Of((JToken)"no", true));
            Assert.IsTrue(LvnExpression.EvaluateBool("flag",
                new System.Collections.Generic.Dictionary<string, JToken> { ["flag"] = "no" }),
                "в выражении непустая строка истинна — и это другое правило");
        }
    }
}
