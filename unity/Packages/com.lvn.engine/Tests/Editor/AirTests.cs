using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ВОЗДУХ — <see cref="LvnAir"/>.
    ///
    /// <para>Проверяется одно: ось трогает ОБЕ свои стороны и не трогает
    /// чужие. Это и есть вся ценность дома — четыре строки, где легко
    /// поправить три, становятся одной, которую нельзя поправить наполовину.
    /// Перекос от забытой четвёртой строки не роняет тест и не краснеет: он
    /// виден только глазами и только на устройстве.</para>
    /// </summary>
    public class AirTests
    {
        // Undefined значит «значение поставили», Null — «не трогали».
        private static bool Тронуто(StyleLength v) => v.keyword == StyleKeyword.Undefined;

        [Test]
        public void Ось_X_трогает_обе_свои_стороны()
        {
            var el = new VisualElement();
            LvnAir.PadX(el, 12f);

            Assert.AreEqual(12f, el.style.paddingLeft.value.value);
            Assert.AreEqual(12f, el.style.paddingRight.value.value);
            Assert.IsFalse(Тронуто(el.style.paddingTop), "ось X не смеет трогать верх");
            Assert.IsFalse(Тронуто(el.style.paddingBottom));
        }

        [Test]
        public void Ось_Y_трогает_обе_свои_стороны()
        {
            var el = new VisualElement();
            LvnAir.PadY(el, 7f);

            Assert.AreEqual(7f, el.style.paddingTop.value.value);
            Assert.AreEqual(7f, el.style.paddingBottom.value.value);
            Assert.IsFalse(Тронуто(el.style.paddingLeft));
        }

        [Test]
        public void Отступ_всех_сторон_равен_двум_осям()
        {
            var осями = new VisualElement();
            LvnAir.PadX(осями, 9f);
            LvnAir.PadY(осями, 9f);

            var целиком = new VisualElement();
            LvnAir.Pad(целиком, 9f);

            Assert.AreEqual(целиком.style.paddingLeft.value.value, осями.style.paddingLeft.value.value);
            Assert.AreEqual(целиком.style.paddingTop.value.value, осями.style.paddingTop.value.value);
            Assert.AreEqual(целиком.style.paddingBottom.value.value, осями.style.paddingBottom.value.value);
        }

        [Test]
        public void Внешний_отступ_не_путается_с_внутренним()
        {
            var el = new VisualElement();
            LvnAir.MarginX(el, 5f);

            Assert.AreEqual(5f, el.style.marginLeft.value.value);
            Assert.AreEqual(5f, el.style.marginRight.value.value);
            Assert.IsFalse(Тронуто(el.style.paddingLeft),
                "воздух снаружи и воздух внутри — разные вещи, и путать их нельзя");
        }

        [Test]
        public void Ноль_это_значение_а_не_пропуск()
        {
            var el = new VisualElement();
            LvnAir.Pad(el, 4f);
            LvnAir.PadY(el, 0f);

            Assert.AreEqual(0f, el.style.paddingTop.value.value,
                "«обнулить» — такая же работа, как «поставить»: строка, доставшаяся "
                + "от прежнего состояния, обязана уйти");
            Assert.AreEqual(4f, el.style.paddingLeft.value.value, "чужая ось не тронута");
        }

        [Test]
        public void Пустой_элемент_не_роняет()
        {
            Assert.DoesNotThrow(() => LvnAir.Pad(null, 1f));
            Assert.DoesNotThrow(() => LvnAir.PadX(null, 1f));
            Assert.DoesNotThrow(() => LvnAir.MarginY(null, 1f));
        }
    }
}
