using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// РЕЖИМ ПОДГОНКИ — ЗАКРЫТЫЙ СПИСОК СЛОВ.
    ///
    /// <para>Слово уходило в мост как есть, а мост берёт «width» на всё, чего
    /// не узнал. Автор писал своё и получал подгонку по ширине без единого
    /// слова объяснения — и шёл искать ошибку в арте.</para>
    ///
    /// <para>Замер 07.09 на живом каталоге: у спайн-сущности стоит
    /// <c>"fit": true</c> — не слово, а «да». Поле объявлено строкой, значение
    /// приезжает как «True», не совпадает ни с одним режимом и молча уходит в
    /// умолчание.</para>
    ///
    /// <para>Проверка для приёмки: верните передачу <c>e.spine.fit</c> в мост
    /// без разбора — упадут НепонятноеСловоНазвано и
    /// НепонятноеСловоНеВыдаётсяЗаРежим.</para>
    /// </summary>
    public class SpineFitWordTests
    {
        [SetUp]
        public void SetUp() => LvnClosedWord.Reset();

        [TearDown]
        public void TearDown() => LvnClosedWord.Reset();

        [TestCase("width")]
        [TestCase("height")]
        [TestCase("cover")]
        [TestCase("contain")]
        public void ИзвестныйРежимПроходитКакЕсть(string режим)
        {
            Assert.AreEqual(режим, VnStage.SpineFit(режим));
            Assert.IsEmpty(LvnClosedWord.Unclaimed, "known word must not complain");
        }

        [TestCase(null)]
        [TestCase("")]
        public void НеСказаноЭтоНеОшибка(string режим)
        {
            Assert.AreEqual(режим, VnStage.SpineFit(режим));
            Assert.IsEmpty(LvnClosedWord.Unclaimed, "отсутствие режима — законное умолчание, а не промах автора");
        }

        [Test]
        public void НепонятноеСловоНазвано()
        {
            // Ровно то, что лежит в живом каталоге: булево «да» вместо слова.
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("spine.fit"));
            VnStage.SpineFit("True");
            Assert.IsTrue(LvnClosedWord.Unclaimed.ContainsKey("spine.fit=True"),
                "непонятое слово обязано попасть в счёт, иначе автор о нём не узнает");
        }

        [Test]
        public void НепонятноеСловоНеВыдаётсяЗаРежим()
        {
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("spine.fit"));
            Assert.IsNull(VnStage.SpineFit("во весь экран"),
                "чужое слово не должно уехать в мост: там оно молча станет «width»");
        }

        [Test]
        public void ОдноСловоЖалуетсяОдинРаз()
        {
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("spine.fit"));
            for (int i = 0; i < 5; i++) VnStage.SpineFit("True");
            Assert.AreEqual(5, LvnClosedWord.Unclaimed["spine.fit=True"],
                "счёт ведётся весь, а говорится один раз — иначе журнал утонет");
        }

        [Test]
        public void СказаноЧтоВЗЯТОВМЕСТО_АНеЧтоНичегоНеПроизошло()
        {
            // Подгонка не пропадает, она идёт по ширине. Сообщение «команда не
            // сделала НИЧЕГО» отправило бы автора искать отсутствующий эффект,
            // которого нет: эффект есть, просто не тот.
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("взято width"));
            VnStage.SpineFit("True");
        }
    }
}
