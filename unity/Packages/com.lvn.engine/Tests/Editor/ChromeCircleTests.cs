using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// КРУГ — ОДНО РЕШЕНИЕ, А НЕ ТРИ ЧИСЛА (<see cref="LvnChrome.Circle"/>,
    /// <see cref="LvnChrome.Pill"/>).
    ///
    /// <para>Круглых элементов в оболочке восемь, и каждый собирался руками:
    /// ширина, высота и скругление тремя строками. Связь между ними нигде не
    /// была записана, и держалась она на двух совпадениях сразу — «радиус из
    /// темы оказался ровно половиной размера» и «а если не оказался, UITK
    /// зажмёт его половиной коробки». Оба совпадения переживают правку темы, но
    /// не переживают правку размера.</para>
    ///
    /// <para>Ценно здесь ровно одно: радиус СЛЕДУЕТ из диаметра, а не стоит
    /// рядом с ним. Перепутать «круг» и «квадрат со скруглением» можно, не
    /// сломав ни одного другого теста, — экран при этом выглядит почти так
    /// же.</para>
    /// </summary>
    public class ChromeCircleTests
    {
        private static bool Задан(StyleLength v) => v.keyword == StyleKeyword.Undefined;

        [TestCase(10f)]
        [TestCase(34f)]
        [TestCase(50f)]
        [TestCase(56f)]
        public void РадиусСледуетИзДиаметра(float d)
        {
            var el = new VisualElement();
            LvnChrome.Circle(el, d);

            Assert.AreEqual(d, el.style.width.value.value, "ширина — диаметр");
            Assert.AreEqual(d, el.style.height.value.value, "высота — диаметр");
            foreach (var r in new[]
                     {
                         el.style.borderTopLeftRadius, el.style.borderTopRightRadius,
                         el.style.borderBottomLeftRadius, el.style.borderBottomRightRadius,
                     })
                Assert.AreEqual(d * 0.5f, r.value.value,
                    "радиус круга — ровно половина диаметра, а не число из темы");
        }

        /// <summary>Круг возвращает СВОЙ элемент: доводка (цвет, выравнивание)
        /// продолжается цепочкой, а не отдельной строкой ниже.</summary>
        [Test]
        public void КругВозвращаетСвойЭлемент()
        {
            var el = new VisualElement();
            Assert.AreSame(el, LvnChrome.Circle(el, 20f));
        }

        /// <summary>Пустой элемент — не падение. Огранку зовут безусловно
        /// (см. заголовок LvnChrome): приём, который надо оборачивать в «если»,
        /// рано или поздно забудут обернуть.</summary>
        [Test]
        public void ПустогоЭлементаХватаетМолча()
        {
            Assert.IsNull(LvnChrome.Circle<VisualElement>(null, 20f));
            Assert.IsNull(LvnChrome.Pill<VisualElement>(null, 20f));
        }

        /// <summary>
        /// ПИЛЮЛЯ ДЛИНУ НЕ ТРОГАЕТ — этим она и отличается от круга.
        ///
        /// <para>Дорожка прокрутки и её бегунок одной ширины и разной высоты:
        /// длину задаёт раскладка (флекс, абсолютные края), и подставить сюда
        /// круг значило бы отобрать у неё эту длину.</para>
        /// </summary>
        [Test]
        public void ПилюляСкругляетНеТрогаяРазмер()
        {
            var el = new VisualElement();
            LvnChrome.Pill(el, 8f);

            Assert.IsFalse(Задан(el.style.width), "пилюля назначила ширину — это дело раскладки");
            Assert.IsFalse(Задан(el.style.height), "пилюля назначила высоту — это дело раскладки");
            Assert.AreEqual(4f, el.style.borderTopLeftRadius.value.value,
                "скругление пилюли — половина короткой стороны");
        }
    }
}
