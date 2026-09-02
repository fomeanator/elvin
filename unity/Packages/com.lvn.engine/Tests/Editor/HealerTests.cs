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
        private bool _working;

        [SetUp]
        public void SetUp()
        {
            _doc = new LvnHealer();
            _sick = false;
            _cures = 0;
            _working = false;
        }

        private void Watch(float period = 1f, float patience = 0f)
            => _doc.Watch("недуг", () => _sick, () => _cures++, period, patience);

        /// <summary>Недуг, у которого есть кого спросить «а не везут ли уже».</summary>
        private void WatchWithWorker(float period = 1f, float patience = 0f)
            => _doc.Watch("недуг", () => _sick, () => _cures++, period, patience,
                          working: () => _working);

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

        // Лечение — попытка, а не обязательство: упавшее не должно стоить
        // обхода соседям.
        [Test]
        public void ABrokenCureDoesNotStopTheRoundEither()
        {
            _doc.Watch("падучее лечение", () => true, () => throw new System.Exception("лечение упало"));
            Watch();
            _sick = true;
            _doc.Tick(0f);
            _doc.Tick(1f);
            Assert.AreEqual(1, _cures, "упавшее лечение утащило за собой весь обход");
        }

        // Сцена ушла — сторож уходит с ней, иначе он лечит мёртвое дерево.
        [Test]
        public void ForgettingAnAilmentStopsTheWatch()
        {
            Watch();
            _sick = true;
            _doc.Tick(0f);
            _doc.Forget("недуг");
            _doc.Tick(1f);

            Assert.AreEqual(0, _cures);
            Assert.AreEqual(0, _doc.HealedCount("недуг"));
        }

        [Test]
        public void ClearForgetsEverySentinelAtOnce()
        {
            Watch();
            _doc.Watch("второй", () => true, () => { });
            _sick = true;
            _doc.Clear();
            _doc.Tick(0f);
            _doc.Tick(1f);

            Assert.AreEqual(0, _cures);
            StringAssert.Contains("под наблюдением никого", _doc.Journal());
        }

        // Наблюдение без имени, без проверки или без лечения — не наблюдение:
        // молча принятая половинка сторожа не сработает и не пожалуется.
        [Test]
        public void AHalfDescribedAilmentIsNotWatched()
        {
            _doc.Watch(null, () => true, () => _cures++);
            _doc.Watch("безпроверки", null, () => _cures++);
            _doc.Watch("безлечения", () => true, null);

            _doc.Tick(0f);
            _doc.Tick(1f);

            Assert.AreEqual(0, _cures);
            StringAssert.Contains("под наблюдением никого", _doc.Journal());
        }

        // Счётчик «лечили» спрашивают по имени: неизвестное имя — ноль, а не
        // чужое число.
        [Test]
        public void AnUnknownAilmentWasHealedZeroTimes()
        {
            Watch();
            Assert.AreEqual(0, _doc.HealedCount("такого недуга нет"));
        }

        // ПОКА ВЕЗУТ — НЕ ЛЕЧИМ. Живой случай: полотно витрины качается десять
        // секунд на слабом телефоне, терпение объявлено в две. Лечение
        // забирает у фона поколение и начинает лестницу повторов заново — то
        // есть ломает ровно тот механизм, который должен был пережить обрыв.
        [Test]
        public void WhileTheWorkIsUnderwayNothingIsHealed()
        {
            WatchWithWorker(period: 0.5f, patience: 2f);
            _sick = true;
            _working = true;
            for (float t = 0f; t < 30f; t += 0.5f) _doc.Tick(t);
            Assert.AreEqual(0, _cures,
                "лекарь перебил живую загрузку — терпение победило факт");
        }

        // ТЕРПЕНИЕ СЧИТАЕТСЯ ОТ КОНЦА РАБОТЫ, А НЕ ОТ НАЧАЛА БОЛЕЗНИ. Иначе
        // десятисекундная загрузка приезжает в мир, где терпение кончилось
        // восемь секунд назад, и первый же кадр после неё — лечение.
        [Test]
        public void PatienceStartsWhenTheWorkStops()
        {
            WatchWithWorker(period: 0.5f, patience: 2f);
            _sick = true;
            _working = true;
            for (float t = 0f; t < 10f; t += 0.5f) _doc.Tick(t);
            _working = false;
            _doc.Tick(10f);
            _doc.Tick(11f);
            Assert.AreEqual(0, _cures, "терпение не начали заново — вылечили в первый же кадр");
            // Ровно 11.5 — терпение (2с) от конца работы (9.5). Окно закрыто
            // до второго лечения нарочно: проверяем начало отсчёта, а не
            // разрежение повторов, у которого свой тест.
            for (float t = 11.5f; t < 13f; t += 0.5f) _doc.Tick(t);
            Assert.AreEqual(1, _cures, "работа кончилась, а недуг остался — лечить обязаны");
        }

        // РАБОТА КОНЧИЛАСЬ УСПЕХОМ — лечить нечего и счёт ожиданий не мешает
        // выздоровлению.
        [Test]
        public void WorkThatSucceedsLeavesNoTreatment()
        {
            WatchWithWorker(period: 0.5f, patience: 1f);
            _sick = true;
            _working = true;
            for (float t = 0f; t < 8f; t += 0.5f) _doc.Tick(t);
            _sick = false;
            _working = false;
            for (float t = 8f; t < 16f; t += 0.5f) _doc.Tick(t);
            Assert.AreEqual(0, _cures, "картинка доехала сама, а её всё равно лечили");
        }

        // НЕ У КОГО СПРОСИТЬ — ведём себя как раньше. Недуг без этого вопроса
        // (их большинство) не должен ни ждать вечно, ни лечиться иначе.
        [Test]
        public void AnAilmentWithNoWorkerBehavesAsBefore()
        {
            Watch(period: 0.5f, patience: 1f);
            _sick = true;
            for (float t = 0f; t < 5f; t += 0.5f) _doc.Tick(t);
            Assert.Greater(_cures, 0, "недуг без погрузчика перестал лечиться");
        }

        // СЛОМАННЫЙ ВОПРОС НЕ ЗАПИРАЕТ ЛЕЧЕНИЕ. Проверка, которая бросает,
        // означала бы «везут всегда» — то есть тихо выключала бы самолечение
        // насовсем, а это худший исход из возможных.
        [Test]
        public void AThrowingWorkerDoesNotBlockHealing()
        {
            _doc.Watch("недуг", () => _sick, () => _cures++, 0.5f, 1f,
                       working: () => throw new System.InvalidOperationException("сломан"));
            _sick = true;
            for (float t = 0f; t < 5f; t += 0.5f) _doc.Tick(t);
            Assert.Greater(_cures, 0, "сломанный вопрос «везут?» выключил лечение");
        }

        // ОЖИДАНИЯ ПОПАДАЮТ В ЖУРНАЛ. Число «ждали N» — мера того, насколько
        // объявленное терпение разошлось с настоящей работой: оно и есть повод
        // не подкручивать секунды, а спросить факт.
        [Test]
        public void TheJournalNamesTheWaiting()
        {
            WatchWithWorker(period: 0.5f, patience: 1f);
            _sick = true;
            _working = true;
            for (float t = 0f; t < 6f; t += 0.5f) _doc.Tick(t);
            StringAssert.Contains("ждали", _doc.Journal(),
                "журнал молчит о том, что лекарь хотел вмешаться в живую работу");
        }

        // Разрежение не уходит в бесконечность: у него объявленный потолок.
        [Test]
        public void TheSpacingHasADeclaredCeiling()
        {
            Watch(period: 0.5f);
            _sick = true;
            for (float t = 0f; t < 120f; t += 0.5f) _doc.Tick(t);

            // За две минуты при потолке 8 секунд лечений не может быть больше,
            // чем «первые несколько + 120/8».
            // Без потолка их было бы 240; с потолком в 8 секунд — два десятка.
            Assert.Less(_cures, 10 + 120f / LvnHealer.MaxPeriod,
                "разрежение перестало расти — лог снова заваливается");
            Assert.Greater(_cures, 3, "и совсем сдаваться Лекарь не должен");
        }
    }
}
