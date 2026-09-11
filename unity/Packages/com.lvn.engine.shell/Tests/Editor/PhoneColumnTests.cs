using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Shell.Tests
{
    /// <summary>
    /// ПОЛОСА ОБОЛОЧКИ НЕ ШИРЕ ТЕЛЕФОНА. Интерфейс нарисован на холсте 1080;
    /// на планшете холст тянется на всю ширину, и логотип, лента и карточки
    /// расплываются вбок. Сторожим оба обещания правила: потолок ширины и
    /// автоматические поля, которыми полоса встаёт по центру.
    /// </summary>
    public class PhoneColumnTests
    {
        [Test]
        public void TheColumnStopsAtThePhoneWidth()
        {
            var el = new VisualElement();
            ScreenUi.PhoneColumn(el);
            Assert.AreEqual(ScreenUi.PhoneWidth, el.style.maxWidth.value.value, 0.01f,
                "шире холста, на котором оболочка нарисована, она не становится");
        }

        [Test]
        public void TheColumnCentresItself()
        {
            var el = new VisualElement();
            ScreenUi.PhoneColumn(el);
            // ЦЕНТРИРУЕМ СДВИГОМ, А НЕ ПОЛЯМИ. Полосу вешают на элемент,
            // растянутый абсолютно (left = right = 0), а в такой раскладке
            // автоматические поля остаток НЕ делят — полоса молча прижималась к
            // левому краю. Правило сменилось вместе с этим наблюдением, и тест
            // держит теперь его: половина ширины плюс сдвиг на половину себя.
            Assert.AreEqual(50f, el.style.left.value.value, 0.01f,
                "полоса должна начинаться от середины — иначе центра не выйдет");
            Assert.AreEqual(LengthUnit.Percent, el.style.left.value.unit);
            Assert.AreEqual(-50f, el.style.translate.value.x.value, 0.01f,
                "остаток делится поровну сдвигом — иначе полоса прижмётся к краю");
            Assert.AreEqual(Lvn.UI.LvnPanel.ReferenceWidth, el.style.maxWidth.value.value, 0.01f,
                "полоса шире телефона — строка расплывается и её нельзя читать");
        }

        [Test]
        public void NoElementIsNotAnError()
        {
            Assert.DoesNotThrow(() => ScreenUi.PhoneColumn(null),
                "правило зовут из сборки экранов — пустой корень не должен её ронять");
        }
    }
}
