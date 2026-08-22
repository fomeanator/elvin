using Newtonsoft.Json.Linq;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// КОНТРАКТ ПОСТАНОВКИ вида drift: «правый уходит вправо, левый — влево».
    /// Направление не пишется автором — оно выводится из позиции, и если правило
    /// однажды перевернётся, персонажи начнут уходить СКВОЗЬ сцену, к центру.
    /// Глазами это читается как «что-то не так», причём непонятно что; тестом —
    /// как перевёрнутый знак.
    /// </summary>
    public class DriftTransitionTests
    {
        [Test]
        public void RightCharacterDriftsRight()
        {
            Assert.AreEqual(1f, LvnFade.DriftSign(0.75f));
            Assert.AreEqual(1f, LvnFade.DriftSign(0.5f), "центр считается правым — у сцены нет третьей стороны");
        }

        [Test]
        public void LeftCharacterDriftsLeft()
        {
            Assert.AreEqual(-1f, LvnFade.DriftSign(0.25f));
            Assert.AreEqual(-1f, LvnFade.DriftSign(0f));
        }

        [Test]
        public void DriftParsesFromTheScript()
        {
            Assert.AreEqual(TransitionType.Drift, VnStage.ParseTransition("drift"));
            Assert.AreEqual(TransitionType.Drift, VnStage.ParseTransition("side"),
                "синоним side — для авторов, которым «drift» ничего не говорит");
        }

        /// <summary>ВХОД НАПРАВЛЕННЫЙ, УХОД — НА МЕСТЕ. Герой въезжает от своей
        /// стороны: это постановка, зритель понимает, откуда он пришёл. Уход
        /// боком владелец отменил явно — второе боковое путешествие подряд
        /// читается как катание по сцене, а не как «ушёл». Предмет и там и там
        /// просто проявляется: реквизит не изображает персонажа.</summary>
        [Test]
        public void ThemeDefaults_ActorsEnterWithDriftAndLeaveWithFade()
        {
            var theme = new VnTheme();
            var actor = Placement.Standing(0.25f);
            var obj = Placement.Standing(0.5f);

            VnStage.ApplyTransitionDefaults(new JObject { ["op"] = "actor" }, theme, ref actor);
            VnStage.ApplyTransitionDefaults(new JObject { ["op"] = "obj" }, theme, ref obj);

            Assert.AreEqual(TransitionType.Drift, actor.EnterTransition);
            Assert.AreEqual(TransitionType.Fade, actor.ExitTransition,
                "уход по умолчанию — растворение на месте, а не второй проезд вбок");
            Assert.AreEqual(TransitionType.Fade, obj.EnterTransition);
            Assert.AreEqual(TransitionType.Fade, obj.ExitTransition);
            Assert.AreEqual(theme.ObjectTransition, obj.TransitionDuration);
        }

        [Test]
        public void ExplicitTransition_OverridesThemeDefaults()
        {
            var p = Placement.Standing(0.75f);
            var cmd = new JObject
            {
                ["op"] = "actor",
                ["enter"] = "fade",
                ["exit"] = "dissolve",
                ["transition_duration"] = 1.25f,
            };
            p.EnterTransition = VnStage.ParseTransition((string)cmd["enter"]);
            p.ExitTransition = VnStage.ParseTransition((string)cmd["exit"]);
            p.TransitionDuration = (float)cmd["transition_duration"];

            VnStage.ApplyTransitionDefaults(cmd, new VnTheme(), ref p);

            Assert.AreEqual(TransitionType.Fade, p.EnterTransition);
            Assert.AreEqual(TransitionType.Dissolve, p.ExitTransition);
            Assert.AreEqual(1.25f, p.TransitionDuration);
        }
    }
}
