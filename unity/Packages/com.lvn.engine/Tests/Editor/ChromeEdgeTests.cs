using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// КРОМКА С ОДНОЙ СТОРОНЫ — <see cref="LvnChrome.EdgeOn"/> и три её роли.
    ///
    /// <para>Толщина и цвет здесь ПАРА, и проверяется именно она: кромка
    /// нулевой толщины невидима любого цвета, а цветная без толщины не
    /// рисуется вовсе. Порознь они значат «полработы» — и по месту терялись
    /// поодиночке именно так.</para>
    /// </summary>
    public class ChromeEdgeTests
    {
        // Толщина кромки — StyleFloat, скругление — StyleLength: у UITK это
        // разные типы, и общего помощника на оба не бывает.
        // У UITK ключевое слово читается наоборот, чем кажется: значение,
        // которое ПОСТАВИЛИ, помечено Undefined («ключевого слова нет, есть
        // число»), а нетронутое — Null. Перепутать легко, и тогда тест
        // проверяет ровно противоположное тому, что написано в его имени.
        private static bool Пусто(StyleFloat v) => v.keyword == StyleKeyword.Null;

        [Test]
        public void Кромка_ставит_толщину_и_цвет_одной_стороне()
        {
            var el = new VisualElement();
            LvnChrome.EdgeOn(el, LvnSide.Left, Color.red, 3f);

            Assert.AreEqual(3f, el.style.borderLeftWidth.value);
            Assert.AreEqual(Color.red, el.style.borderLeftColor.value);
            Assert.IsTrue(Пусто(el.style.borderRightWidth), "тронута лишняя сторона");
            Assert.IsTrue(Пусто(el.style.borderTopWidth));
            Assert.IsTrue(Пусто(el.style.borderBottomWidth));
        }

        [Test]
        public void Разделитель_по_умолчанию_снизу()
        {
            var el = new VisualElement();
            LvnChrome.Divider(el);

            Assert.AreEqual(1f, el.style.borderBottomWidth.value);
            Assert.AreEqual(LvnTokens.Border, el.style.borderBottomColor.value,
                "тон разделителя берётся из темы, а не выбирается на месте");
            Assert.IsTrue(Пусто(el.style.borderTopWidth));
        }

        [Test]
        public void Полоска_гасит_остальные_три_стороны()
        {
            var el = new VisualElement();
            LvnChrome.Border(el, Color.green, 4f);   // строка досталась от прежнего состояния
            LvnChrome.Stripe(el);

            Assert.AreEqual(3f, el.style.borderLeftWidth.value);
            Assert.AreEqual(LvnTokens.Accent, el.style.borderLeftColor.value);
            Assert.AreEqual(0f, el.style.borderTopWidth.value,
                "чужая рамка обязана уйти: полоска значит «только слева»");
            Assert.AreEqual(0f, el.style.borderRightWidth.value);
            Assert.AreEqual(0f, el.style.borderBottomWidth.value);
        }

        [Test]
        public void Крышка_это_кромка_сверху_акцентом()
        {
            var el = new VisualElement();
            LvnChrome.Lid(el);

            Assert.AreEqual(LvnTokens.Accent, el.style.borderTopColor.value);
            Assert.Greater(el.style.borderTopWidth.value, 0f,
                "крышка без толщины не рисуется — а это её единственная работа");
            Assert.IsTrue(Пусто(el.style.borderBottomWidth));
        }

        [Test]
        public void Пустой_элемент_не_роняет()
        {
            Assert.DoesNotThrow(() => LvnChrome.EdgeOn(null, LvnSide.Top, Color.red, 1f));
            Assert.DoesNotThrow(() => LvnChrome.Divider(null));
            Assert.DoesNotThrow(() => LvnChrome.Stripe(null));
        }
    }
}
