using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАКРЫТОЕ СЛОВО, КОТОРОГО НЕТ В СПИСКЕ, — НЕ МОЛЧАНИЕ.
    ///
    /// <para>Часть авторских значений — закрытый перечень. Разбираются они
    /// перечислением случаев, и у перечисления есть тихий исход: слово не
    /// совпало ни с одним — значит не произошло НИЧЕГО. Автор пишет
    /// «justify=middle», видит вёрстку по умолчанию и уходит искать ошибку в
    /// другом месте.</para>
    ///
    /// <para>Движок уже умеет так про КОМАНДЫ: незнакомый op считается и уходит
    /// в отчёт, потому что «узнавать об этом надо не от игрока».</para>
    /// </summary>
    public sealed class ClosedWordTests
    {
        [SetUp] public void Забыть() => LvnClosedWord.Reset();
        [TearDown] public void Убрать() => LvnClosedWord.Reset();

        [Test]
        public void НезнакомоеСловоСчитаетсяИНазывается()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("justify.*middle"));

            LvnClosedWord.Unknown("justify", "middle", "center | end");

            Assert.AreEqual(1, LvnClosedWord.Unclaimed["justify=middle"],
                "незнакомое слово не посчитано — по одному предупреждению не понять, опечатка это или редкость");
        }

        // Опечатка в цикле перерисовки повторится сотни раз, а сказать надо
        // однажды и внятно: консоль, залитая одной строкой, читается как шум.
        [Test]
        public void ГоворитсяОдинРазЗаСессию()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("align"));

            for (int i = 0; i < 5; i++) LvnClosedWord.Unknown("align", "middle", "center | end");

            Assert.AreEqual(5, LvnClosedWord.Unclaimed["align=middle"], "счёт обязан идти дальше");
        }

        // «Не сказано» — это не ошибка: у большинства узлов раскладка не задана
        // вовсе, и жаловаться было бы не на что.
        [Test]
        public void ПустоеСловоНеЖалоба()
        {
            LvnClosedWord.Unknown("justify", null, "center | end");
            LvnClosedWord.Unknown("justify", "", "center | end");

            Assert.AreEqual(0, LvnClosedWord.Unclaimed.Count);
        }
    }
}
