using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ДЕВЯТИСЛОЙКА — <see cref="LvnPicture.Slice"/>.
    ///
    /// <para>Проверяется порядок сторон у формы с четырьмя числами: тема
    /// хранит срез вектором (x слева, y справа, z сверху, w снизу), и
    /// перепутанный порядок не падает — он даёт рамку, у которой хвостик
    /// бабла оказывается сверху. Такое находят глазами и не сразу.</para>
    /// </summary>
    public class PictureSliceTests
    {
        [Test]
        public void Одно_число_режет_все_четыре_стороны()
        {
            var el = new VisualElement();
            LvnPicture.Slice(el, 16);

            Assert.AreEqual(16, el.style.unitySliceLeft.value);
            Assert.AreEqual(16, el.style.unitySliceRight.value);
            Assert.AreEqual(16, el.style.unitySliceTop.value);
            Assert.AreEqual(16, el.style.unitySliceBottom.value);
        }

        [Test]
        public void Порядок_сторон_в_векторе_как_у_темы()
        {
            var el = new VisualElement();
            LvnPicture.Slice(el, new Vector4(1f, 2f, 3f, 4f));

            Assert.AreEqual(1, el.style.unitySliceLeft.value, "x — слева");
            Assert.AreEqual(2, el.style.unitySliceRight.value, "y — справа");
            Assert.AreEqual(3, el.style.unitySliceTop.value, "z — сверху");
            Assert.AreEqual(4, el.style.unitySliceBottom.value, "w — снизу");
        }

        [Test]
        public void Масштаб_среза_ставится_обеими_формами()
        {
            var одним = new VisualElement();
            LvnPicture.Slice(одним, 8, 0.5f);
            Assert.AreEqual(0.5f, одним.style.unitySliceScale.value);

            var вектором = new VisualElement();
            LvnPicture.Slice(вектором, new Vector4(8f, 8f, 8f, 8f), 0.5f);
            Assert.AreEqual(0.5f, вектором.style.unitySliceScale.value,
                "обе формы обязаны ставить масштаб: без него арт под плотный экран съедает содержимое");
        }

        [Test]
        public void Пустой_элемент_не_роняет()
        {
            Assert.DoesNotThrow(() => LvnPicture.Slice(null, 4));
            Assert.DoesNotThrow(() => LvnPicture.Slice(null, Vector4.one));
        }
    }
}
