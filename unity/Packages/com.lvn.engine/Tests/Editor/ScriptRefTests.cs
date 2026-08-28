using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// «ЭТО ТОТ ЖЕ СКРИПТ?» — записи адреса расходятся там, где сохранение
    /// встречается с сегодняшним манифестом.
    /// </summary>
    public class ScriptRefTests
    {
        // «/c/Глава1.lvn» процентами и буквами.
        private const string Percent = "/c/%D0%93%D0%BB%D0%B0%D0%B2%D0%B01.lvn";
        private const string Plain = "/c/\u0413\u043b\u0430\u0432\u04301.lvn";

        [Test]
        public void PercentAndPlain_AreTheSameScript()
            => Assert.IsTrue(LvnScriptRef.Same(Percent, Plain),
                "сейв с процентами в адресе объявлялся чужим");

        [Test]
        public void DecomposedAndComposed_AreTheSameScript()
        {
            // «/c/Майя.lvn» кодами: «й» разложенной формой (и + краткая) против
            // собранной. Буквами писать нельзя — редактор пересоберёт обе.
            const string decomposed = "/c/\u041c\u0430\u0438\u0306\u044f.lvn";
            const string composed = "/c/\u041c\u0430\u0439\u044f.lvn";
            Assert.AreNotEqual(decomposed, composed, "фикстура собрана — тест ничего не ловит");
            Assert.IsTrue(LvnScriptRef.Same(decomposed, composed));
        }

        [Test]
        public void DifferentChapters_StayDifferent()
        {
            Assert.IsFalse(LvnScriptRef.Same("/c/ch1.lvn", "/c/ch2.lvn"),
                "смягчение сравнения не должно сливать разные главы");
            Assert.IsFalse(LvnScriptRef.Same("/c/ch1.lvn", "/other/ch1.lvn"));
        }

        [Test]
        public void Empty_IsNotAMatch()
        {
            // «Адреса нет» — не «адреса совпали»: иначе пустой снимок подошёл бы
            // к любой главе.
            Assert.IsFalse(LvnScriptRef.Same(null, "/c/ch1.lvn"));
            Assert.IsFalse(LvnScriptRef.Same("", ""));
        }

        [Test]
        public void CacheBustingQuery_DoesNotChangeTheScript()
        {
            // «?v=3» ставят, чтобы обновить кэш; глава от этого другой не стала.
            Assert.IsTrue(LvnScriptRef.Same("/c/ch1.lvn?v=3", "/c/ch1.lvn"));
            Assert.IsTrue(LvnScriptRef.Same("/c/ch1.lvn?v=3", "/c/ch1.lvn?v=4"));
            Assert.IsFalse(LvnScriptRef.Same("/c/ch1.lvn?v=3", "/c/ch2.lvn?v=3"));
        }

        [Test]
        public void PlusStaysAPlus()
        {
            // UnEscapeURL превратил бы «+» в пробел (наследие веб-форм), и файл
            // «a+b.lvn» перестал бы совпадать сам с собой.
            Assert.IsTrue(LvnScriptRef.Same("/c/a+b.lvn", "/c/a+b.lvn"));
            Assert.IsFalse(LvnScriptRef.Same("/c/a+b.lvn", "/c/a b.lvn"));
        }
    }
}
