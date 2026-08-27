using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests.Editor
{
    /// <summary>
    /// ЛЕКАРЬ — один дом для всех самолечений сцены.
    ///
    /// <para>Проверяется то, чем самолечение отличается от дёрганья: оно ждёт,
    /// прежде чем вмешаться (загрузка имеет право доехать сама), не молотит
    /// без конца по неизлечимому и умеет сказать, что лечило. Каждое правило
    /// здесь выведено живым дефектом: страж, лечивший слишком рано, перебивал
    /// живую постановку; страж без счётчика оставлял вопрос «а это вообще
    /// случалось?» без ответа.</para>
    /// </summary>
    public class HealerTests
    {
        private LvnHealer _doc;
        private bool _sick;
        private int _cures;

        [SetUp]
        public void SetUp()
        {
            _doc = new LvnHealer();
            _sick = false;
            _cures = 0;
        }

        private void Watch(float period = 1f, float patience = 0f)
            => _doc.Watch("недуг", () => _sick, () => _cures++, period, patience);

        [Test]
        public void AHealthySceneIsNeverTouched()
        {
            Watch();
            for (float t = 0f; t < 10f; t += 1f) _doc.Tick(t);
            Assert.AreEqual(0, _cures, "лечили здорового");
            Assert.AreEqual(0, _doc.Healings);
        }

        [Test]
        public void ASickSceneGetsHealed()
        {
            Watch();
            _sick = true;
            _doc.Tick(0f);     // первый взгляд: заметили
            _doc.Tick(1f);     // второй: лечим
            Assert.AreEqual(1, _cures);
        }

        // ЖИВОЙ СЛУЧАЙ: крупный канвас декодится ~0.6с. Лечить его в этот
        // момент — значит перебивать живую загрузку своей.
        [Test]
        public void PatienceLetsTheSceneFinishOnItsOwn()
        {
            Watch(period: 0.5f, patience: 2f);
            _sick = true;
            for (float t = 0f; t < 2f; t += 0.5f) _doc.Tick(t);
            Assert.AreEqual(0, _cures, "вмешались раньше, чем дали доехать");

            _doc.Tick(2.5f);
            Assert.AreEqual(1, _cures, "терпение вышло, а лечения нет");
        }

        [Test]
        public void GettingBetterResetsThePatience()
        {
            Watch(period: 0.5f, patience: 2f);
            _sick = true;
            _doc.Tick(0f);
            _doc.Tick(0.5f);
            _sick = false;
            _doc.Tick(1f);       // выздоровел — отсчёт обнулён
            _sick = true;
            _doc.Tick(1.5f);
            _doc.Tick(2.5f);     // от нового начала прошла всего секунда
            Assert.AreEqual(0, _cures, "терпение считалось от старого недомогания");
        }

        [Test]
        public void TheDoctorLooksNoOftenerThanAsked()
        {
            int looks = 0;
            _doc.Watch("редкий", () => { looks++; return false; }, () => { }, period: 2f);
            for (float t = 0f; t < 2f; t += 0.1f) _doc.Tick(t);
            Assert.AreEqual(1, looks, "смотрел чаще, чем просили");
        }

        // НЕИЗЛЕЧИМОЕ НЕ МОЛОТИМ. Лечение, которое не помогает, — тоже диагноз:
        // сыпать им каждые полсекунды значит завалить лог и отобрать кадры у
        // игры вместо честного «не лечится».
        [Test]
        public void WhatDoesNotHealIsTriedEverMoreRarely()
        {
            Watch(period: 0.5f);
            _sick = true;
            for (float t = 0f; t < 10f; t += 0.5f) _doc.Tick(t);
            Assert.Less(_cures, 12, "молотили по неизлечимому без передышки");
            Assert.Greater(_cures, 2, "сдались после первой же неудачи");
            StringAssert.Contains("НЕ ЛЕЧИТСЯ", _doc.Journal());
        }

        // Ради этого Лекарь и заведён: одна строка отвечает, что чинилось само.
        [Test]
        public void TheJournalSaysWhatHealedItself()
        {
            Watch();
            StringAssert.Contains("лечить не пришлось", _doc.Journal());

            _sick = true;
            _doc.Tick(0f);
            _doc.Tick(1f);
            _sick = false;
            _doc.Tick(2f);

            var journal = _doc.Journal();
            StringAssert.Contains("недуг", journal);
            StringAssert.Contains("лечений 1", journal);
            Assert.AreEqual(1, _doc.HealedCount("недуг"));
        }

        // Сцена пересобирается — второй сторож того же имени означал бы двойное
        // лечение одного и того же недуга.
        [Test]
        public void WatchingTheSameAilmentTwiceKeepsOneDoctor()
        {
            Watch();
            Watch();
            _sick = true;
            _doc.Tick(0f);
            _doc.Tick(1f);
            Assert.AreEqual(1, _cures, "недуг лечили дважды за один обход");
        }

        [Test]
        public void ABrokenCheckDoesNotStopTheRound()
        {
            _doc.Watch("сломанный", () => throw new System.Exception("проверка упала"), () => { });
            Watch();
            _sick = true;
            _doc.Tick(0f);
            _doc.Tick(1f);
            Assert.AreEqual(1, _cures, "упавшая проверка утащила за собой весь обход");
        }
    }
}
