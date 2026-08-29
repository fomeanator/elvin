using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЧИСЛО В ПОДПИСИ — обе записи места и ни одного молчаливого промаха.
    ///
    /// <para>Живой дефект: движок писал место числом <c>{0}</c>, а отсчёты
    /// времени — <c>{n}</c>, и вторая запись уже разошлась по готовым каталогам
    /// переводов. Пока дом слов знал только первую, перевод со второй записью
    /// МОЛЧА получал число, приклеенное в конец фразы: «через {n} мин 5».
    /// Переводчик видит свою строку в каталоге целой и не понимает, почему на
    /// экране мусор.</para>
    ///
    /// <para>Дописывание через пробел — не запасной случай «на всякий», а
    /// последняя линия: шаблон без места числа всё равно обязан ДОВЕЗТИ число
    /// до экрана. «Осталось» без числа хуже, чем «Осталось 5» с неудачным
    /// порядком слов.</para>
    /// </summary>
    public sealed class WordsPlaceholderTests
    {
        [TearDown]
        public void Forget() => LvnWords.Learn(null);

        [Test]
        public void ЗнаетОбеЗаписиМеста()
        {
            LvnWords.Learn(new Dictionary<string, string>
            {
                ["day"] = "День {0}",
                ["left"] = "через {n} мин",
            });

            Assert.AreEqual("День 3", LvnWords.Of("day", "Day {0}", 3));
            Assert.AreEqual("через 5 мин", LvnWords.Of("left", "in {n} min", 5),
                "перевод со второй записью получал число, приклеенное в конец: «через {n} мин 5»");
        }

        [Test]
        public void ОбеЗаписиРаботаютИВАнглийскомУмолчании()
        {
            // Умолчание движка — та же строка того же дома: если знать обе
            // записи только в словаре новеллы, промах вернётся с другой стороны.
            Assert.AreEqual("Day 3", LvnWords.Of("no.such.key", "Day {0}", 3));
            Assert.AreEqual("in 5 min", LvnWords.Of("no.such.key", "in {n} min", 5));
        }

        [Test]
        public void БезМестаЧислоДописываетсяЧерезПробел()
        {
            // Автор забыл место — число всё равно обязано доехать до экрана.
            Assert.AreEqual("Осталось 5", LvnWords.Of("no.such.key", "Осталось", 5));
        }

        [Test]
        public void ПустойШаблонНеПадает()
        {
            // Пустая подпись — законная: так автор ГАСИТ строку интерфейса.
            // Дописать к ней число значило бы вернуть на экран « 5».
            Assert.AreEqual("", LvnWords.Of("no.such.key", "", 5));
            Assert.IsNull(LvnWords.Of("no.such.key", null, 5));

            LvnWords.Learn(new Dictionary<string, string> { ["hidden"] = "" });
            Assert.AreEqual("", LvnWords.Of("hidden", "", 5));
        }

        [Test]
        public void NullВАргументеНеРоняетПодпись()
        {
            // Число приходит из данных и иногда не приходит вовсе. Подпись без
            // числа — плохо; исключение посреди сборки экрана — хуже.
            Assert.AreEqual("День ", LvnWords.Of("no.such.key", "День {0}", null));
            Assert.AreEqual("через  мин", LvnWords.Of("no.such.key", "через {n} мин", null));
            Assert.AreEqual("Осталось ", LvnWords.Of("no.such.key", "Осталось", null));
        }

        [Test]
        public void ЗаписьМестаБерётсяИзПереводаАНеИзУмолчания()
        {
            // Переводчик вправе выбрать СВОЮ запись места: умолчание движка
            // пишет «{0}», каталог — «{n}», и наоборот. Обе обязаны сработать
            // против любого умолчания.
            LvnWords.Learn(new Dictionary<string, string> { ["day"] = "{n}-й день" });
            Assert.AreEqual("3-й день", LvnWords.Of("day", "Day {0}", 3));

            LvnWords.Learn(new Dictionary<string, string> { ["day"] = "Tag {0}" });
            Assert.AreEqual("Tag 3", LvnWords.Of("day", "day {n}", 3));
        }
    }
}
