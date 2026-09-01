using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛОВИНЧАТОЕ СКРУГЛЕНИЕ — <see cref="LvnChrome.RoundTop"/> и
    /// <see cref="LvnChrome.RoundBottom"/>.
    ///
    /// <para>Правило не декоративное: картинка, вросшая в шапку карточки,
    /// обязана повторить её скругление СВОИМИ верхними углами, иначе прямые
    /// углы вылезают наружу. Ценно здесь ровно одно — что тронуты ДВА угла из
    /// четырёх: «скруглить верх» и «скруглить всё» отличаются только этим, а
    /// перепутать их можно, не сломав ни одного другого теста.</para>
    /// </summary>
    public class ChromeCornerTests
    {
        // Undefined значит «ключевого слова нет, есть число», то есть
        // значение ПОСТАВИЛИ; нетронутое поле помечено Null. Читается наоборот
        // интуиции — см. LvnChrome и ChromeEdgeTests.
        private static bool Задан(StyleLength v) => v.keyword == StyleKeyword.Undefined;

        [Test]
        public void Верх_скруглён_низ_не_тронут()
        {
            var el = new VisualElement();
            LvnChrome.RoundTop(el, 12f);

            Assert.AreEqual(12f, el.style.borderTopLeftRadius.value.value);
            Assert.AreEqual(12f, el.style.borderTopRightRadius.value.value);
            Assert.IsFalse(Задан(el.style.borderBottomLeftRadius),
                "низ обязан остаться прямым — иначе это Round, а не RoundTop");
            Assert.IsFalse(Задан(el.style.borderBottomRightRadius));
        }

        [Test]
        public void Низ_скруглён_верх_не_тронут()
        {
            var el = new VisualElement();
            LvnChrome.RoundBottom(el, 8f);

            Assert.AreEqual(8f, el.style.borderBottomLeftRadius.value.value);
            Assert.AreEqual(8f, el.style.borderBottomRightRadius.value.value);
            Assert.IsFalse(Задан(el.style.borderTopLeftRadius));
            Assert.IsFalse(Задан(el.style.borderTopRightRadius));
        }

        [Test]
        public void Верх_и_низ_вместе_дают_то_же_что_Round()
        {
            var половинами = new VisualElement();
            LvnChrome.RoundTop(половинами, 16f);
            LvnChrome.RoundBottom(половинами, 16f);

            var целиком = new VisualElement();
            LvnChrome.Round(целиком, 16f);

            Assert.AreEqual(целиком.style.borderTopLeftRadius.value.value,
                половинами.style.borderTopLeftRadius.value.value);
            Assert.AreEqual(целиком.style.borderBottomRightRadius.value.value,
                половинами.style.borderBottomRightRadius.value.value);
        }

        [Test]
        public void Пустой_элемент_не_роняет()
        {
            Assert.DoesNotThrow(() => LvnChrome.RoundTop(null, 4f));
            Assert.DoesNotThrow(() => LvnChrome.RoundBottom(null, 4f));
        }
    }
}
