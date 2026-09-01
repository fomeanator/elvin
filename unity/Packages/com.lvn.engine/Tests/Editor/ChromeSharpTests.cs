using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ОСТРЫЕ УГЛЫ — <see cref="LvnChrome.Sharp"/>.
    ///
    /// <para>Ноль в скруглении значит не «ступень шкалы, равная нулю», а
    /// «скругления здесь нет вовсе»: под элементом арт, который сам рисует свои
    /// края. Написанный числом, он был неотличим от радиуса, подобранного на
    /// глаз, — и храповик радиусов считал его наравне.</para>
    /// </summary>
    public class ChromeSharpTests
    {
        [Test]
        public void Все_четыре_угла_прямые()
        {
            var el = new VisualElement();
            LvnChrome.Round(el, 16f);
            LvnChrome.Sharp(el);

            Assert.AreEqual(0f, el.style.borderTopLeftRadius.value.value);
            Assert.AreEqual(0f, el.style.borderTopRightRadius.value.value);
            Assert.AreEqual(0f, el.style.borderBottomLeftRadius.value.value);
            Assert.AreEqual(0f, el.style.borderBottomRightRadius.value.value,
                "прежнее скругление обязано уйти целиком: арт рисует края сам");
        }

        [Test]
        public void Пустой_элемент_не_роняет()
            => Assert.DoesNotThrow(() => LvnChrome.Sharp(null));
    }
}
