using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests.Editor
{
    /// <summary>
    /// РАСПИСАНИЕ КАДРА: кто в каком месте чем является — для каждой точки
    /// сценария.
    ///
    /// <para>«Управление спрайтами идёт через сценарий и поэтому становится
    /// непредсказуемым; нужно строгое расписание, кто в каком месте чем
    /// является, и функция, возвращающая состояние сцены» (Илья, 27.08).
    /// Проверки держат три вещи: кадр читается как ДАННЫЕ, состояние в любой
    /// точке восстановимо без переигрывания, а на ветвлениях расписание честно
    /// признаёт неоднозначность вместо того, чтобы выбрать один вариант
    /// молча.</para>
    /// </summary>
    public class FrameScheduleTests
    {
        private static JObject Show(string id, string pos = "center") => new JObject
        {
            ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = pos,
        };
        private static JObject Hide(string id) => new JObject
        {
            ["op"] = "actor", ["id"] = id, ["show"] = false,
        };
        private static JObject Fx(string id, float dark) => new JObject
        {
            ["op"] = "sfx", ["id"] = id, ["dark"] = dark,
        };
        private static JObject FxOff(string id) => new JObject
        {
            ["op"] = "sfx", ["id"] = id, ["off"] = 1,
        };
        private static JObject Say(string text) => new JObject { ["op"] = "say", ["text"] = text };
        private static JObject Bg(string url) => new JObject { ["op"] = "bg", ["sprite_url"] = url };

        // Линейная цепочка: каждый следующий шаг — наследник предыдущего.
        private static System.Func<int, IReadOnlyList<int>> Linear(int n)
            => i => i + 1 < n ? new[] { i + 1 } : new int[0];

        // ── кадр как данные ─────────────────────────────────────────────────

        [Test]
        public void FrameRemembersWhoIsOnStageAndHow()
        {
            var f = new LvnFrame();
            f.Absorb(Bg("/bg/hall.jpg"));
            f.Absorb(Show("agent", "left"));
            f.Absorb(Fx("agent", 0.88f));

            Assert.IsTrue(f.Actors.ContainsKey("agent"));
            Assert.IsTrue(f.Actors["agent"].Visible);
            Assert.AreEqual("left", (string)f.Actors["agent"].Pose["position"]);
            Assert.AreEqual(0.88f, (float)f.Actors["agent"].Fx["dark"], 0.001f,
                "грим не записан в кадр — именно поэтому он и терялся при возврате");
        }

        // ЖИВОЙ СЛУЧАЙ: Агент вернулся после катсцены без своего тёмного грима —
        // обычным человеком в белой рубашке рядом со своим же силуэтом.
        [Test]
        public void HidingKeepsTheLookSoItCanComeBackWhole()
        {
            var f = new LvnFrame();
            f.Absorb(Show("agent"));
            f.Absorb(Fx("agent", 0.88f));
            f.Absorb(Hide("agent"));

            Assert.IsFalse(f.Actors["agent"].Visible, "ушёл — значит не виден");
            Assert.IsNotNull(f.Actors["agent"].Fx,
                "грим стёрся вместе с уходом — вернётся другой человек");
        }

        [Test]
        public void TakingTheLookOffIsAnAnswerNotAbsence()
        {
            var f = new LvnFrame();
            f.Absorb(Fx("agent", 0.88f));
            f.Absorb(FxOff("agent"));
            Assert.IsNull(f.Actors["agent"].Fx, "`off` обязан снимать грим, а не становиться им");
        }

        // `clear` — это «скрыть всех», а не «забыть всех»: показанный снова без
        // position обязан встать на своё прежнее место.
        [Test]
        public void ClearHidesEveryoneButKeepsTheirPlaces()
        {
            var f = new LvnFrame();
            f.Absorb(Show("agent", "left"));
            f.Absorb(Show("hero", "right"));
            f.Absorb(new JObject { ["op"] = "clear" });

            Assert.AreEqual(2, f.Actors.Count, "кадр забыл людей — вернуть их будет нечем");
            Assert.IsFalse(f.Actors["agent"].Visible);
            Assert.AreEqual("left", (string)f.Actors["agent"].Pose["position"]);
        }

        // ── расписание по графу ─────────────────────────────────────────────

        [Test]
        public void EveryStopKnowsItsFrame()
        {
            var script = new List<JObject> { Bg("/x.jpg"), Show("agent"), Say("привет"), Hide("agent") };
            var stops = LvnFrameSchedule.Build(script, Linear(script.Count));

            Assert.IsTrue(stops[1].Frame.Actors["agent"].Visible, "на шаге 1 агент в кадре");
            Assert.IsTrue(stops[2].Frame.Actors["agent"].Visible, "реплика не должна убирать людей");
            Assert.IsFalse(stops[3].Frame.Actors["agent"].Visible, "на шаге 3 он ушёл");
            foreach (var s in stops) Assert.IsTrue(s.Certain, "линейный сценарий однозначен весь");
        }

        // Ветви сошлись с ОДИНАКОВОЙ сценой — знание подтверждено, а не потеряно.
        [Test]
        public void BranchesThatAgreeStayCertain()
        {
            //   0 показать агента
            //   1 ветвь A: реплика      3 схождение
            //   2 ветвь B: реплика      3
            var script = new List<JObject> { Show("agent"), Say("A"), Say("B"), Say("вместе") };
            IReadOnlyList<int> Edges(int i)
                => i == 0 ? new[] { 1, 2 } : i == 1 || i == 2 ? new[] { 3 } : new int[0];

            var stops = LvnFrameSchedule.Build(script, Edges);

            Assert.IsTrue(stops[3].Certain, "ветви привели одинаковую сцену — узел однозначен");
            Assert.IsTrue(stops[3].Frame.Actors["agent"].Visible);
        }

        // Ветви сошлись с РАЗНОЙ сценой — расписание обязано это признать, а не
        // выбрать один вариант молча: ложь в расписании опаснее пустого места.
        [Test]
        public void BranchesThatDisagreeAreMarkedUncertain()
        {
            //   0 показать агента
            //   1 ветвь A: показать героиню   3 схождение
            //   2 ветвь B: спрятать агента    3
            var script = new List<JObject> { Show("agent"), Show("hero"), Hide("agent"), Say("вместе") };
            IReadOnlyList<int> Edges(int i)
                => i == 0 ? new[] { 1, 2 } : i == 1 || i == 2 ? new[] { 3 } : new int[0];

            var stops = LvnFrameSchedule.Build(script, Edges);

            Assert.IsFalse(stops[3].Certain,
                "кадр зависит от пути, а расписание уверяет, что знает его");
        }

        // ── состояние по сохранению ─────────────────────────────────────────

        // Точный ответ там, где расписание неоднозначно: сохранение хранит путь,
        // которым игрок реально шёл, и по нему кадр считается без догадок и без
        // побочных эффектов — ни звука, ни ожиданий, ни переходов.
        [Test]
        public void TheTraceGivesTheExactFrameForASave()
        {
            var script = new List<JObject>
            {
                Show("agent"),        // 0
                Show("hero"),         // 1 — ветвь, которую игрок НЕ выбрал
                Hide("agent"),        // 2 — ветвь, которую выбрал
                Say("вместе"),        // 3
            };

            var frame = LvnFrameSchedule.At(script, new[] { 0, 2, 3 });

            Assert.IsFalse(frame.Actors["agent"].Visible, "по этому пути агент ушёл");
            Assert.IsFalse(frame.Actors.ContainsKey("hero"),
                "героиню показывала другая ветвь — в этом сохранении её нет");
        }

        [Test]
        public void AnEmptyOverlayChangesNothing()
        {
            var f = new LvnFrame();
            Assert.IsTrue(f.IsEmpty, "пустое наложение обязано быть пустым — иначе оно сотрёт кадр под собой");
            f.Exclusive = true;
            Assert.IsFalse(f.IsEmpty, "«в кадре только мои» — это уже требование, а не пустота");
        }
    }
}
