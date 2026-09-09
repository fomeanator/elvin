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
            Assert.AreEqual(StyleKeyword.Auto, el.style.marginLeft.keyword,
                "остаток делится поровну — иначе полоса прижмётся к краю");
            Assert.AreEqual(StyleKeyword.Auto, el.style.marginRight.keyword);
        }

        [Test]
        public void NoElementIsNotAnError()
        {
            Assert.DoesNotThrow(() => ScreenUi.PhoneColumn(null),
                "правило зовут из сборки экранов — пустой корень не должен её ронять");
        }
    }
}
