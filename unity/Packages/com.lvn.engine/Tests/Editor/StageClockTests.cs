using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Хронометрист — правило «кто опоздал», проверяемое без сцены и без
    /// ожидания реального времени: часы гоняются вручную.
    public class StageClockTests
    {
        private LvnStageClock _clock;
        private float _now;

        [SetUp]
        public void Setup()
        {
            _now = 100f;
            _clock = new LvnStageClock { Now = () => _now };
        }

        // ── эпоха ─────────────────────────────────────────────────────────────

        [Test]
        public void Work_FromAPreviousScene_LosesTheRightToDraw()
        {
            int mine = _clock.Epoch;          // работа началась в этой сцене
            Assert.IsTrue(_clock.IsCurrent(mine));

            _clock.NewEpoch();                // сменилась глава, пока грузился арт

            Assert.IsFalse(_clock.IsCurrent(mine),
                "поздний арт прошлой сцены не смеет рисовать на новой");
        }

        [Test]
        public void NewScene_DropsEveryBarrier()
        {
            _clock.Hold(LvnStageClock.ActorExitBarrier, 5f);
            Assert.Greater(_clock.Remaining(LvnStageClock.ActorExitBarrier), 0f);

            _clock.NewEpoch();

            Assert.IsTrue(_clock.Passed(LvnStageClock.ActorExitBarrier),
                "ждать уходов сцены, которой больше нет, — это зависшая новая сцена");
        }

        // ── дорожки ───────────────────────────────────────────────────────────

        [Test]
        public void OnALane_OnlyTheNewestMayTouchTheScreen()
        {
            var lane = LvnStageClock.ActorLane("hill");
            int first = _clock.Claim(lane);    // показ поехал за артом
            int second = _clock.Claim(lane);   // игрок пролистал эмоцию — новый показ

            Assert.IsFalse(_clock.IsNewest(lane, first),
                "старый наряд не имеет права выиграть, приехав позже");
            Assert.IsTrue(_clock.IsNewest(lane, second));
        }

        [Test]
        public void Lanes_DoNotShoutOverEachOther()
        {
            var hill = LvnStageClock.ActorLane("hill");
            var agent = LvnStageClock.ActorLane("agent");
            int hillTicket = _clock.Claim(hill);
            _clock.Claim(agent);
            _clock.Claim(agent);

            Assert.IsTrue(_clock.IsNewest(hill, hillTicket),
                "смена одного актёра не отменяет показ другого");
        }

        [Test]
        public void MayTouch_NeedsBothTheSceneAndTheLane()
        {
            var lane = LvnStageClock.ActorLane("hill");
            int epoch = _clock.Epoch;
            int ticket = _clock.Claim(lane);
            Assert.IsTrue(_clock.MayTouch(epoch, lane, ticket));

            _clock.Claim(lane);                                  // обогнали на дорожке
            Assert.IsFalse(_clock.MayTouch(epoch, lane, ticket));

            int fresh = _clock.Claim(lane);                      // снова новейший…
            _clock.NewEpoch();                                   // …но сцена уже другая
            Assert.IsFalse(_clock.MayTouch(epoch, lane, fresh));
        }

        // Регресс первой версии Хронометриста: сброс сцены обнулял ДОРОЖКИ
        // вместе с барьерами, и работа, начатая до сброса, снова оказывалась
        // «новейшей» — счётчик возвращался к её номеру. Единственная защита от
        // опоздавшего в том, что его номер уже никогда не повторится.
        [Test]
        public void NewScene_DoesNotRewindLaneNumbers()
        {
            var lane = LvnStageClock.ActorLane("hill");
            int old = _clock.Claim(lane);      // показ поехал за артом

            _clock.NewEpoch();                 // сменилась глава
            int fresh = _clock.Claim(lane);    // новая сцена ставит того же актёра

            Assert.AreNotEqual(old, fresh, "номер на дорожке обязан только расти");
            Assert.IsFalse(_clock.IsNewest(lane, old),
                "работа прошлой сцены не смеет снова стать новейшей");
        }

        [Test]
        public void ReleaseAll_DropsBarriersButKeepsLaneNumbers()
        {
            var lane = LvnStageClock.WaitLane;
            int ticket = _clock.Claim(lane);
            _clock.Claim(lane);                 // ticket устарел
            _clock.Hold(LvnStageClock.ActorExitBarrier, 3f);

            _clock.ReleaseAll();

            Assert.IsTrue(_clock.Passed(LvnStageClock.ActorExitBarrier), "барьеры сняты");
            Assert.IsFalse(_clock.IsNewest(lane, ticket), "а память дорожек цела");
        }

        [Test]
        public void Cancel_RetiresAWaitWithoutStartingOne()
        {
            int ticket = _clock.Claim(LvnStageClock.WaitLane);
            _clock.Cancel(LvnStageClock.WaitLane);   // тап по горячей точке

            Assert.IsFalse(_clock.IsNewest(LvnStageClock.WaitLane, ticket),
                "отменённое ожидание не должно потом само шагнуть вперёд");
        }

        [Test]
        public void UnknownTicket_IsTreatedAsCurrent_SoNothingDeadlocks()
        {
            Assert.IsTrue(_clock.IsNewest("никто-не-занимал", 0),
                "спросить про дорожку, которой не было, — не повод замереть");
        }

        // ── барьеры ───────────────────────────────────────────────────────────

        [Test]
        public void Barrier_HoldsForItsTime_AndThenLetsGo()
        {
            _clock.Hold(LvnStageClock.ActorExitBarrier, 0.4f);
            Assert.AreEqual(0.4f, _clock.Remaining(LvnStageClock.ActorExitBarrier), 1e-4f);

            _now += 0.3f;
            Assert.AreEqual(0.1f, _clock.Remaining(LvnStageClock.ActorExitBarrier), 1e-4f);

            _now += 0.2f;
            Assert.IsTrue(_clock.Passed(LvnStageClock.ActorExitBarrier));
            Assert.AreEqual(0f, _clock.Remaining(LvnStageClock.ActorExitBarrier), 1e-4f,
                "просроченный барьер не уходит в минус");
        }

        [Test]
        public void TwoLeavingActors_MakeTheBarrierWaitForTheSlower()
        {
            _clock.Hold(LvnStageClock.ActorExitBarrier, 0.5f);
            _clock.Hold(LvnStageClock.ActorExitBarrier, 0.2f);   // второй уходит быстрее

            Assert.AreEqual(0.5f, _clock.Remaining(LvnStageClock.ActorExitBarrier), 1e-4f,
                "барьер продлевается, а не переустанавливается — иначе вход обгонит уход");
        }

        [Test]
        public void SwapBarrier_IsPerActor()
        {
            _clock.Hold(LvnStageClock.SwapBarrier("hill"), 0.3f);

            Assert.Greater(_clock.Remaining(LvnStageClock.SwapBarrier("hill")), 0f);
            Assert.IsTrue(_clock.Passed(LvnStageClock.SwapBarrier("agent")),
                "кроссфейд одной героини не задерживает показ другой");
        }

        [Test]
        public void Release_LetsGoEarly()
        {
            _clock.Hold(LvnStageClock.ActorVisibilityBarrier, 5f);
            _clock.Release(LvnStageClock.ActorVisibilityBarrier);

            Assert.IsTrue(_clock.Passed(LvnStageClock.ActorVisibilityBarrier));
        }

        [Test]
        public void ZeroOrNegativeHold_IsNotABarrier()
        {
            _clock.Hold(LvnStageClock.ActorExitBarrier, 0f);
            _clock.Hold(LvnStageClock.ActorExitBarrier, -1f);

            Assert.IsTrue(_clock.Passed(LvnStageClock.ActorExitBarrier),
                "мгновенный переход не имеет права держать сцену");
        }
    }
}
