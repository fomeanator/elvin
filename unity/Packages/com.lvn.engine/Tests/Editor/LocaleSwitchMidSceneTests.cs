using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СМЕНА ЯЗЫКА ПОСРЕДИ СЦЕНЫ.
    ///
    /// <para>Обещание не «движок умеет каталоги», а «игрок открыл настройки
    /// посреди разговора, переключил язык и увидел ТУ ЖЕ реплику на новом —
    /// сразу, а не со следующей строки». Он открыл настройки ровно ради
    /// этого.</para>
    ///
    /// <para>Соседние тесты (<c>LvnLocaleTests</c>) проверяют ВЫБОР языка:
    /// какой побеждает, что при откате, что показывает кольцо в настройках.
    /// Здесь — сам обмен текста под работающей главой.</para>
    /// </summary>
    public class LocaleSwitchMidSceneTests
    {
        private sealed class Screen : ILvnStage
        {
            public readonly List<string> Shown = new List<string>();
            public string Last => Shown.Count > 0 ? Shown[Shown.Count - 1] : null;
            public void ShowSay(string who, string text, string style) => Shown.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command, LvnSender sender) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private const string Chapter = @"{""script"":[
            {""op"":""say"",""text"":""Good evening.""},
            {""op"":""say"",""text"":""The rain has stopped.""}
        ]}";

        private static LvnPlayer Open(out Screen screen)
        {
            screen = new Screen();
            return new LvnPlayer(LvnDocument.Parse(Chapter), screen);
        }

        private static Dictionary<string, string> Ru => new Dictionary<string, string>
        {
            ["Good evening."] = "Добрый вечер.",
            ["The rain has stopped."] = "Дождь кончился.",
        };

        // ГЛАВНОЕ: реплика, УЖЕ СТОЯЩАЯ НА ЭКРАНЕ, меняет язык сразу.
        [Test]
        public void РепликаНаЭкранеПереводитсяСразуАНеСоСледующей()
        {
            var p = Open(out var screen);
            p.Advance();
            Assert.AreEqual("Good evening.", screen.Last, "без каталога — исходный текст");

            // Игрок открыл настройки и переключил язык.
            p.Strings = Ru;
            p.RerenderCurrent();

            Assert.AreEqual("Добрый вечер.", screen.Last,
                "перевелась следующая реплика вместо той, что перед глазами: "
                + "игрок переключил язык и увидел прежний текст");
        }

        // И дальше глава идёт уже на новом языке.
        [Test]
        public void ОстальнаяГлаваИдётНаНовомЯзыке()
        {
            var p = Open(out var screen);
            p.Advance();
            p.Strings = Ru;
            p.RerenderCurrent();
            p.Advance();

            Assert.AreEqual("Дождь кончился.", screen.Last, "следующая реплика осталась на старом языке");
        }

        // ПРОПУСК В КАТАЛОГЕ — НЕ ПУСТОЙ ЭКРАН. Непереведённая строка обязана
        // показать исходник: полперевода лучше дыры в разговоре.
        [Test]
        public void НепереведённаяСтрокаПоказываетИсходник()
        {
            var p = Open(out var screen);
            p.Strings = new Dictionary<string, string> { ["Good evening."] = "Добрый вечер." };
            p.Advance();
            Assert.AreEqual("Добрый вечер.", screen.Last);
            p.Advance();
            Assert.AreEqual("The rain has stopped.", screen.Last,
                "строка без перевода потерялась вместо того, чтобы остаться исходной");
        }

        // ВАРИАНТ НЕ СДВИГАЕТСЯ ОТ ПЕРЕРИСОВКИ. Строка вида {a|b} выбирает
        // вариант; смена языка перерисовывает реплику, и если бы перерисовка
        // считалась новым показом, текст менялся бы у игрока на глазах сам по
        // себе — при каждой смене языка и каждой пересборке интерфейса.
        [Test]
        public void ПеререндерНеДвигаетВариантСтроки()
        {
            var screen = new Screen();
            var p = new LvnPlayer(LvnDocument.Parse(
                @"{""script"":[{""op"":""say"",""text"":""{Rain|Snow|Fog} again.""}]}"), screen);
            p.Advance();
            var first = screen.Last;

            p.RerenderCurrent();
            p.RerenderCurrent();

            Assert.AreEqual(first, screen.Last,
                "перерисовка сдвинула вариант: текст меняется сам по себе при смене языка");
        }
    }
}
