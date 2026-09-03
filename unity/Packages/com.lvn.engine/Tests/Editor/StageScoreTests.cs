using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests.Editor
{
    /// <summary>
    /// ПАРТИТУРА СЦЕНЫ: слои владения кадром.
    ///
    /// <para>Главная проверка здесь одна — «вернуть как было» перестало быть
    /// работой. Катсцена не уводит и не возвращает: она открывает наложение и
    /// закрывает его, а кадр истории проступает сам, потому что его никто не
    /// стирал. Каждый живой дефект недели — «агент пропал на несколько ходов»,
    /// «вернулся без грима», «героиня не уходит, хотя не её реплика» — был
    /// пропущенным пунктом в ручном возврате.</para>
    /// </summary>
    public class StageScoreTests
    {
        private LvnStageScore _score;

        [SetUp]
        public void SetUp() => _score = new LvnStageScore();

        private static JObject Pose(string id, string pos = "center") => new JObject
        {
            ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = pos,
        };
        private static JObject Fx(string id, float dark) => new JObject
        {
            ["op"] = "sfx", ["id"] = id, ["dark"] = dark,
        };

        private void StoryShows(string id, string pos = "center")
            => _score.Layer(LvnSender.Story).Absorb(Pose(id, pos));

        private void StoryLook(string id, float dark)
            => _score.Layer(LvnSender.Story).Absorb(Fx(id, dark));

        private List<string> Visible() => _score.Compose().Visible();

        // ── композиция ──────────────────────────────────────────────────────

        [Test]
        public void TheStoryFrameIsWhatTheScriptBuilt()
        {
            StoryShows("agent");
            StoryShows("hero");
            CollectionAssert.AreEqual(new[] { "agent", "hero" }, Visible());
        }

        // ЖИВОЙ СЛУЧАЙ: катсцена оставляет одну героиню — остальные обязаны
        // ИСЧЕЗНУТЬ С ЭКРАНА, но не из кадра истории.
        [Test]
        public void AnExclusiveOverlayHidesTheRestWithoutErasingThem()
        {
            StoryShows("agent");
            StoryShows("hero");

            var cut = _score.Layer(LvnSender.Cutscene);
            cut.Exclusive = true;
            cut.Actors["hero"] = new LvnFrame.Actor { Visible = true };

            CollectionAssert.AreEqual(new[] { "hero" }, Visible(),
                "катсцена оставила в кадре кого-то ещё");

            _score.Close(LvnSender.Cutscene);
            CollectionAssert.AreEqual(new[] { "agent", "hero" }, Visible(),
                "кадр истории не вернулся сам — значит наложение его стёрло");
        }

        // ЖИВОЙ СЛУЧАЙ: Агент возвращался после катсцены БЕЗ своего тёмного
        // грима — обычным человеком в белой рубашке рядом со своим силуэтом.
        [Test]
        public void TheLookComesBackWithThePerson()
        {
            StoryShows("agent");
            StoryLook("agent", 0.88f);

            var cut = _score.Layer(LvnSender.Cutscene);
            cut.Exclusive = true;
            cut.Actors["hero"] = new LvnFrame.Actor { Visible = true };
            _score.Close(LvnSender.Cutscene);

            var agent = _score.Compose().Actors["agent"];
            Assert.IsTrue(agent.Visible, "агент не вернулся");
            Assert.IsNotNull(agent.Fx, "агент вернулся без грима — это уже другой человек");
            Assert.AreEqual(0.88f, (float)agent.Fx["dark"], 0.001f);
        }

        // Наложение, поставившее человека, не обязано знать его облик: чего оно
        // не сказало — берётся из кадра под ним. Иначе катсцена молча стирала бы
        // то, о чём вообще не говорила.
        [Test]
        public void AnOverlayInheritsWhatItDidNotSay()
        {
            StoryShows("hero", "left");
            StoryLook("hero", 0.5f);

            var cut = _score.Layer(LvnSender.Cutscene);
            cut.Actors["hero"] = new LvnFrame.Actor { Visible = true };   // ни позы, ни грима

            var hero = _score.Compose().Actors["hero"];
            Assert.AreEqual("left", (string)hero.Pose["position"], "поза потерялась под наложением");
            Assert.IsNotNull(hero.Fx, "грим потерялся под наложением");
        }

        // Старшинство: витрина ниже катсцены, и её кукла не спорит с кадром
        // катсцены. Ровно этим героиня «прыгала» при возврате в меню.
        [Test]
        public void TheCutsceneOutranksTheShowcase()
        {
            _score.Layer(LvnSender.Menu).Absorb(Pose("hero", "center"));
            var cut = _score.Layer(LvnSender.Cutscene);
            cut.Actors["hero"] = new LvnFrame.Actor { Pose = Pose("hero", "right"), Visible = true };

            Assert.AreEqual("right", (string)_score.Compose().Actors["hero"].Pose["position"],
                "витрина перебила катсцену");
        }

        // ── разница вместо пересборки ───────────────────────────────────────

        // Кадр приводится РАЗНИЦЕЙ: сцена, перестроенная целиком на каждое
        // изменение, теряет начатые переходы и перезагружает арт.
        [Test]
        public void NothingChangedMeansNothingToDo()
        {
            StoryShows("agent");
            var screen = _score.Compose();
            CollectionAssert.IsEmpty(_score.DiffAgainst(screen),
                "экран уже совпадает с партитурой, а сцене велено что-то делать");
        }

        [Test]
        public void OnlyTheDifferenceComesBack()
        {
            StoryShows("agent");
            var screen = _score.Compose();

            StoryShows("hero");                       // добавился один человек
            var changes = _score.DiffAgainst(screen);

            Assert.AreEqual(1, changes.Count, "вернулась пересборка вместо разницы");
            Assert.AreEqual("hero", changes[0].Id);
            Assert.IsTrue(changes[0].Show);
        }

        // Кто есть на экране, но кого партитура не знает вовсе, — уходит. Это
        // случай «гость из прошлой главы всплыл в меню».
        [Test]
        public void SomeoneTheScoreDoesNotKnowIsSentAway()
        {
            var screen = new LvnFrame();
            screen.Absorb(Pose("stranger"));

            var changes = _score.DiffAgainst(screen);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("stranger", changes[0].Id);
            Assert.IsFalse(changes[0].Show, "чужак остался в кадре");
        }

        // Уборка сцены сносит все слои: кадр прошлой главы описывает прошлую
        // историю, и «восстановить» его в новой было бы точным исполнением
        // бессмыслицы.
        [Test]
        public void AWipeLeavesNoLayers()
        {
            StoryShows("agent");
            _score.Layer(LvnSender.Cutscene).Actors["hero"] = new LvnFrame.Actor { Visible = true };
            _score.Clear();

            CollectionAssert.IsEmpty(Visible());
            Assert.IsFalse(_score.HasLayer(LvnSender.Cutscene));
        }

        // ОДЕТА ЛИ СЦЕНА ИСТОРИЕЙ. По этому вопросу приезд через створ решает,
        // разыгрывать ли себя: поверх готового кадра (возврат с сохранения)
        // церемония сводилась к «спрятать собеседника и вернуть собеседника» —
        // четыре растворения ради кадра, который и до них стоял правильно.
        [Test]
        public void ПустойСлойИсторииЗначитНачалоГлавы()
        {
            Assert.IsFalse(_score.Dressed(LvnSender.Story), "кадр пуст, а слой считает себя одетым");
        }

        [Test]
        public void ВыставленныйАктёрЗначитКадрСобран()
        {
            StoryShows("agent");
            Assert.IsTrue(_score.Dressed(LvnSender.Story), "актёр в кадре, а слой считает себя пустым");
        }

        // Спрятанный не считается: он в списке слоя, но не в кадре. Иначе
        // приезд молчал бы там, где сцена на деле пуста.
        [Test]
        public void СпрятанныйАктёрКадрНеОдевает()
        {
            StoryShows("agent");
            _score.Layer(LvnSender.Story).Actors["agent"] = new LvnFrame.Actor { Visible = false };
            Assert.IsFalse(_score.Dressed(LvnSender.Story), "спрятанный актёр посчитался за одетый кадр");
        }

        // Чужой слой не отвечает за историю: катсцена выставляет своих, и
        // принять их за собранный кадр значит не разыграть приезд там, где надо.
        [Test]
        public void КатсценаНеОдеваетСлойИстории()
        {
            _score.Layer(LvnSender.Cutscene).Actors["hero"] = new LvnFrame.Actor { Visible = true };
            Assert.IsFalse(_score.Dressed(LvnSender.Story), "актёр катсцены посчитался за кадр истории");
        }
    }
}
