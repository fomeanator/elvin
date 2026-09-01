using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОТОК — <see cref="LvnFlow"/>.
    ///
    /// <para>Ценность дома в двух вещах, и обе проверяются здесь. Первая:
    /// поворот в строку и перенос идут ВМЕСТЕ — ряд без переноса не ошибка, а
    /// молча уезжающий за край экрана. Вторая: выравнивание ставится, только
    /// если названо, — иначе дом был бы негоден там, где ряд пришёл готовым и
    /// выравнивание ему задал кто-то другой.</para>
    /// </summary>
    public class FlowTests
    {
        [Test]
        public void Поворот_и_перенос_идут_вместе()
        {
            var el = LvnFlow.Wrap();

            Assert.AreEqual(FlexDirection.Row, el.style.flexDirection.value);
            Assert.AreEqual(Wrap.Wrap, el.style.flexWrap.value,
                "ряд без переноса уезжает за край экрана, и это не видно в тестах");
        }

        [Test]
        public void Выравнивание_молчит_пока_его_не_назвали()
        {
            var чужой = new VisualElement();
            чужой.style.justifyContent = Justify.SpaceBetween;   // задал ScreenUi.Row(spread)

            LvnFlow.Wrap(чужой);

            Assert.AreEqual(Justify.SpaceBetween, чужой.style.justifyContent.value,
                "дом не смеет чинить перенос ценой чужой вёрстки");
        }

        [Test]
        public void Названное_выравнивание_ставится()
        {
            var el = LvnFlow.Wrap(Justify.Center);
            Assert.AreEqual(Justify.Center, el.style.justifyContent.value);
        }

        [Test]
        public void Готовый_элемент_возвращается_тот_же()
        {
            var el = new VisualElement();
            Assert.AreSame(el, LvnFlow.Wrap(el),
                "дом обустраивает элемент, а не подменяет его: цепочка вызовов должна работать");
        }

        [Test]
        public void Пустой_элемент_не_роняет()
        {
            Assert.DoesNotThrow(() => LvnFlow.Wrap((VisualElement)null));
            Assert.IsNull(LvnFlow.Wrap((VisualElement)null));
        }
    }
}
