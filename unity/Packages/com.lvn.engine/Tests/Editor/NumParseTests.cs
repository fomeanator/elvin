using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// Что считается числом. Проценты здесь не удобство, а починка молчаливой
    /// потери: `x=32%` не разбиралось, поле уходило в null, и объект вставал в
    /// положение по умолчанию — автор видел в тексте одни координаты, а на
    /// экране другие, без единого предупреждения. Поймано на витрине аур:
    /// скелет сел в центр у пола вместо заданного места.
    /// </summary>
    public class NumParseTests
    {
        [Test]
        public void Percent_IsAFraction()
        {
            Assert.AreEqual(0.57f, LvnNum.Parse(JToken.FromObject("57%")).Value, 1e-5f);
            Assert.AreEqual(0.5f, LvnNum.Parse(JToken.FromObject("50 %")).Value, 1e-5f);
            Assert.AreEqual(1f, LvnNum.Parse(JToken.FromObject("100%")).Value, 1e-5f);
        }

        [Test]
        public void PlainNumbers_StillWork()
        {
            Assert.AreEqual(0.3f, LvnNum.Parse(JToken.FromObject(0.3)).Value, 1e-5f);
            Assert.AreEqual(0.3f, LvnNum.Parse(JToken.FromObject("0.3")).Value, 1e-5f);
            Assert.AreEqual(-12f, LvnNum.Parse(JToken.FromObject("-12")).Value, 1e-5f);
        }

        /// <summary>
        /// `scale` МНОЖИТ размер актёра. Поле было объявлено в грамматике,
        /// защищено от осей каста и переживало реплей — и нигде не
        /// применялось: команда компилировалась и молча не делала ничего.
        /// </summary>
        [Test]
        public void ActorScale_MultipliesTheBox()
        {
            var cmd = JObject.Parse("{\"op\":\"actor\",\"id\":\"a\",\"width\":0.4,\"height\":0.8,\"scale\":1.5}");
            var p = Lvn.UI.VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.6f, p.Width.Value, 1e-4f);
            Assert.AreEqual(1.2f, p.Height.Value, 1e-4f);
        }

        [Test]
        public void ActorScale_WorksWithoutExplicitSize()
        {
            var cmd = JObject.Parse("{\"op\":\"actor\",\"id\":\"a\",\"scale\":0.5}");
            var p = Lvn.UI.VnStage.PlacementFrom(cmd);
            // Умножается умолчание темы — иначе scale пришлось бы писать
            // вместе с width/height, то есть считать за автора.
            Assert.AreEqual(Lvn.UI.Placement.DefaultWidth * 0.5f, p.Width.Value, 1e-4f);
        }

        [Test]
        public void Broken_IsNullNotThrow()
        {
            Assert.IsNull(LvnNum.Parse(null));
            Assert.IsNull(LvnNum.Parse(JToken.FromObject("")));
            Assert.IsNull(LvnNum.Parse(JToken.FromObject("почти число")));
            Assert.IsNull(LvnNum.Parse(JToken.FromObject("%")));
            Assert.AreEqual(7f, LvnNum.Parse(JToken.FromObject("ерунда"), 7f), 1e-5f);
        }
    }
}
