using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Режиссёр экрана: интерфейс скрыт, пока держит хоть одна причина, а
    /// «назад» всегда закрывает верхнего.
    public class ScreenDirectorTests
    {
        private LvnScreenDirector _d;
        private int _changes;

        [SetUp]
        public void Setup()
        {
            _d = new LvnScreenDirector();
            _changes = 0;
            _d.Changed += () => _changes++;
        }

        // ── скрытие интерфейса ────────────────────────────────────────────────

        // ЖИВОЙ БАГ, ради которого роль и заводилась: скрытие держал БУЛЕВ
        // флаг, а просили его трое независимо. Игрок отпускал палец посреди
        // катсцены — и интерфейс возвращался в кадр, который его не звал.
        [Test]
        public void OneReasonReleased_DoesNotUndoAnother()
        {
            _d.HideChrome(LvnScreenDirector.CutsceneReason);
            _d.HideChrome(LvnScreenDirector.ArtViewReason);

            _d.ShowChrome(LvnScreenDirector.ArtViewReason);   // палец отпущен

            Assert.IsTrue(_d.ChromeHidden, "катсцена не кончается оттого, что отпустили палец");
            Assert.IsTrue(_d.HiddenBecause(LvnScreenDirector.CutsceneReason));
            Assert.IsFalse(_d.HiddenBecause(LvnScreenDirector.ArtViewReason));

            _d.ShowChrome(LvnScreenDirector.CutsceneReason);  // и катсцена кончилась
            Assert.IsFalse(_d.ChromeHidden);
        }

        [Test]
        public void PeekAndArtView_AreDifferentReasons()
        {
            _d.HideChrome(LvnScreenDirector.PeekReason);      // «во весь рост»
            _d.HideChrome(LvnScreenDirector.ArtViewReason);   // и заодно подержал палец
            _d.ShowChrome(LvnScreenDirector.ArtViewReason);

            Assert.IsTrue(_d.ChromeHidden, "примерка во весь рост продолжается");
        }

        [Test]
        public void SameReasonTwice_IsStillOneReason()
        {
            _d.HideChrome(LvnScreenDirector.CutsceneReason);
            _d.HideChrome(LvnScreenDirector.CutsceneReason);
            _d.ShowChrome(LvnScreenDirector.CutsceneReason);

            Assert.IsFalse(_d.ChromeHidden,
                "повтор просьбы не делает её двумя — иначе интерфейс не вернуть");
        }

        [Test]
        public void HiddenChrome_NeverOutlivesItsScene()
        {
            _d.HideChrome(LvnScreenDirector.CutsceneReason);
            _d.HideChrome(LvnScreenDirector.PeekReason);

            _d.ShowChromeAll();                                // сброс сцены

            Assert.IsFalse(_d.ChromeHidden, "глава кончилась — интерфейс обязан вернуться");
        }

        [Test]
        public void ChangedFires_OnlyWhenTheScreenActuallyChanges()
        {
            _d.HideChrome(LvnScreenDirector.CutsceneReason);   // экран закрылся
            int afterFirst = _changes;
            _d.HideChrome(LvnScreenDirector.ArtViewReason);    // всё ещё закрыт
            Assert.AreEqual(afterFirst, _changes, "вторая причина ничего не меняет на экране");

            _d.ShowChrome(LvnScreenDirector.ArtViewReason);    // и всё ещё закрыт
            Assert.AreEqual(afterFirst, _changes);

            _d.ShowChrome(LvnScreenDirector.CutsceneReason);   // вот теперь открылся
            Assert.AreEqual(afterFirst + 1, _changes);
        }

        // ── режим ─────────────────────────────────────────────────────────────

        [Test]
        public void Mode_IsOneTruthForEveryone()
        {
            Assert.IsFalse(_d.InChapter, "по умолчанию игрок в меню");

            _d.EnterChapter();
            Assert.IsTrue(_d.InChapter);
            int seen = _changes;
            _d.EnterChapter();                 // повторный вход — не событие
            Assert.AreEqual(seen, _changes);

            _d.LeaveChapter();
            Assert.IsFalse(_d.InChapter);
        }

        // ── поверхности ───────────────────────────────────────────────────────

        [Test]
        public void Back_ClosesTheTopmostSurface()
        {
            _d.Open(LvnScreenDirector.StoryPanel);
            _d.Open(LvnScreenDirector.ShellModal);   // магазин поверх гардероба

            Assert.AreEqual(LvnScreenDirector.ShellModal, _d.BackTarget,
                "«назад» закрывает верхнего, а не того, кто открылся первым");

            _d.Close(LvnScreenDirector.ShellModal);
            Assert.AreEqual(LvnScreenDirector.StoryPanel, _d.BackTarget);

            _d.Close(LvnScreenDirector.StoryPanel);
            Assert.IsNull(_d.BackTarget, "экран чист — «назад» принадлежит сцене");
            Assert.IsFalse(_d.AnyOpen);
        }

        [Test]
        public void ReopeningASurface_RaisesIt_DoesNotDuplicate()
        {
            _d.Open(LvnScreenDirector.QuickMenu);
            _d.Open(LvnScreenDirector.StoryPanel);
            _d.Open(LvnScreenDirector.QuickMenu);    // снова наверх

            Assert.AreEqual(LvnScreenDirector.QuickMenu, _d.Top);
            _d.Close(LvnScreenDirector.QuickMenu);
            Assert.IsFalse(_d.IsOpen(LvnScreenDirector.QuickMenu),
                "одно закрытие обязано убрать её целиком, а не снять один из дублей");
        }

        [Test]
        public void ClosingAMiddleSurface_LeavesTheTopAlone()
        {
            _d.Open(LvnScreenDirector.StoryPanel);
            _d.Open(LvnScreenDirector.ShellModal);

            _d.Close(LvnScreenDirector.StoryPanel);   // хост закрыл лист под попапом

            Assert.AreEqual(LvnScreenDirector.ShellModal, _d.Top);
            Assert.IsTrue(_d.AnyOpen);
        }

        [Test]
        public void Reset_ClearsBothChromeAndSurfaces()
        {
            _d.HideChrome(LvnScreenDirector.PeekReason);
            _d.Open(LvnScreenDirector.StoryPanel);

            _d.Reset();

            Assert.IsFalse(_d.ChromeHidden);
            Assert.IsFalse(_d.AnyOpen);
        }
    }
}
