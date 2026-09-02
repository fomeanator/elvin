using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ДЕРЖАТ ПО ПРИЧИНАМ (<see cref="LvnReasons"/>).
    ///
    /// <para>Проверяется ровно то, чем счёт причин отличается от флага: чужую
    /// просьбу снять нельзя. Флаг ломается со ВТОРЫМ держателем и всегда
    /// одинаково — «ушла одна причина, снялись все», — и на экране это не
    /// падение, а «иногда не так»: отпустил палец посреди катсцены, и хром
    /// вернулся.</para>
    ///
    /// <para>Второе, что проверяется, — возвращаемое значение. Оно значит
    /// «состояние ПЕРЕВЕРНУЛОСЬ», а не «просьба принята»: именно на этом
    /// вопросе флаг экономил, заставляя каждого держать свою память о прошлом
    /// значении.</para>
    /// </summary>
    public class ReasonsTests
    {
        private const string Катсцена = "катсцена";
        private const string Палец = "долгое нажатие";

        [Test]
        public void ЧужуюПричинуСнятьНельзя()
        {
            var r = new LvnReasons();
            r.Hold(Катсцена);
            r.Hold(Палец);

            Assert.IsFalse(r.Drop(Палец), "палец отпустили, а держит ещё катсцена");
            Assert.IsTrue(r.Any, "катсцена не кончается оттого, что игрок отпустил палец");
            Assert.IsTrue(r.Drop(Катсцена), "последняя причина ушла — держать больше некому");
            Assert.IsFalse(r.Any);
        }

        [Test]
        public void ПереворотСостоянияВидноПоВозврату()
        {
            var r = new LvnReasons();
            Assert.IsTrue(r.Hold(Катсцена), "первая причина — состояние перевернулось");
            Assert.IsFalse(r.Hold(Палец), "вторая причина ничего не переворачивает");
            Assert.IsFalse(r.Drop(Палец));
            Assert.IsTrue(r.Drop(Катсцена));
        }

        /// <summary>Причина одна, сколько бы раз о ней ни сказали: повтор не
        /// заводит второго держателя и не требует второго снятия.</summary>
        [Test]
        public void ПовторТойЖеПричиныНеСобытие()
        {
            var r = new LvnReasons();
            Assert.IsTrue(r.Hold(Катсцена));
            Assert.IsFalse(r.Hold(Катсцена), "та же причина повторно — не событие");
            Assert.AreEqual(1, r.Count);
            Assert.IsTrue(r.Drop(Катсцена), "одного снятия хватает");
        }

        /// <summary>Снятие того, чего не держали, ничего не меняет — иначе
        /// симметричная пара «взял/отпустил», написанная в двух ветках, роняла
        /// бы чужой замок из ветки, где своего не брали.</summary>
        [Test]
        public void СнятьНевзятоеНичегоНеМеняет()
        {
            var r = new LvnReasons();
            r.Hold(Катсцена);
            Assert.IsFalse(r.Drop(Палец), "не держали — нечего и снимать");
            Assert.IsTrue(r.Has(Катсцена), "чужой замок остался на месте");
        }

        [Test]
        public void СбросСнимаетВсёИСообщаетОбЭтом()
        {
            var r = new LvnReasons();
            r.Hold(Катсцена); r.Hold(Палец);
            Assert.IsTrue(r.Clear(), "что-то действительно сняли");
            Assert.IsFalse(r.Any);
            Assert.IsFalse(r.Clear(), "снимать нечего — и это не событие");
        }

        [Test]
        public void ПустаяПричинаНеДержит()
        {
            var r = new LvnReasons();
            Assert.IsFalse(r.Hold(null));
            Assert.IsFalse(r.Hold(""));
            Assert.IsFalse(r.Any, "безымянная причина держала бы вечно: снять её нечем");
            Assert.IsFalse(r.Has(null));
        }

        /// <summary>Ради журнала причины и названы словами: на вопрос «почему
        /// оно до сих пор выключено» у флага ответа нет вовсе.</summary>
        [Test]
        public void ЖурналНазываетДержащих()
        {
            var r = new LvnReasons();
            Assert.AreEqual("никто", r.Journal());
            r.Hold(Палец); r.Hold(Катсцена);
            var j = r.Journal();
            StringAssert.Contains(Катсцена, j);
            StringAssert.Contains(Палец, j);
        }
    }
}
