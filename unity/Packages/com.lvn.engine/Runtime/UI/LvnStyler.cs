using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// СТИЛИЗАТОР — знает, как выглядит РОЛЬ элемента.
    ///
    /// <para>Экран говорит, ЧТО это («главное действие», «один из вариантов»,
    /// «карточка», «дорожка шкалы»), а не какого оно цвета. Как роль выглядит в
    /// действующей теме — решает стилизатор, и решает в одном месте.</para>
    ///
    /// <para>До него слой выглядел так: <see cref="LvnTokens"/> — словарь
    /// значений, <see cref="LvnChrome"/> — отдельные приёмы огранки (скруглить,
    /// снять рамку, поставить кромку), а СОБИРАЛ из них вид каждый экран сам.
    /// «Покрасить кнопку» было написано трижды — <c>StageMenu.StyleGhost</c>,
    /// <c>SettingsScreen.StyleValueButton</c>, <c>WardrobeSheet.SkinButton</c>,
    /// — и у каждой копии свой источник цвета. Сорок девять файлов красили
    /// элементы вручную. Это не сорок девять видов кнопки: это один вид,
    /// написанный сорок девять раз, и каждый новый экран начинался с того, что
    /// автор решал заново, как он выглядит.</para>
    ///
    /// <para>Граница ответственности проведена нарочно узко: стилизатор ставит
    /// только то, что относится к РОЛИ — плашку, цвет текста, кромку,
    /// скругление. Размеры, отступы, шрифт и жирность остаются экрану: они про
    /// КОМПОНОВКУ этого места, а не про роль, и попытка забрать их сюда сделала
    /// бы стилизатора вторым макетчиком.</para>
    ///
    /// <para>Роли работают и на <c>Button</c>, и на <c>Label</c> — вкладка
    /// бывает и тем, и другим, а выглядеть обязана одинаково. Каждый метод
    /// возвращает тот же элемент, чтобы вставать в цепочку.</para>
    /// </summary>
    public static class LvnStyler
    {
        /// <summary>ГЛАВНОЕ ДЕЙСТВИЕ экрана: акцент темы и чернила поверх него.
        /// На экране такая ровно одна — иначе главного нет.</summary>
        public static T Primary<T>(T el, float radius = -1f) where T : VisualElement
            => Plate(el, LvnTokens.Accent, LvnTokens.OnAccent, radius);

        /// <summary>ВТОРОСТЕПЕННОЕ действие: приглушённая плашка, обычный текст
        /// темы. Рядом с главной такие могут стоять пачкой.</summary>
        public static T Quiet<T>(T el, float radius = -1f) where T : VisualElement
            => Plate(el, LvnTokens.Faint, LvnTokens.Text, radius);

        /// <summary>ПРИЗРАК: плашки нет вовсе — только знак или слово. Так
        /// выглядят «закрыть», «назад» и прочее, чему нельзя спорить с
        /// содержимым за внимание.</summary>
        public static T Ghost<T>(T el, Color? ink = null) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = Color.clear;
            el.style.color = ink ?? LvnTokens.Text;
            LvnChrome.ClearBorder(el);
            return el;
        }

        /// <summary>ОДИН ИЗ ВАРИАНТОВ: выбранный горит акцентом, остальные
        /// приглушены. Вкладки, переключатели, языки, качество — везде, где
        /// выбор ровно один и должен читаться с одного взгляда.</summary>
        public static T Choice<T>(T el, bool chosen, float radius = -1f) where T : VisualElement
            => chosen ? Primary(el, radius) : Quiet(el, radius);

        /// <summary>
        /// ВКЛАДКА — тот же выбор, но с ЖИРНЫМ активным.
        ///
        /// <para>Правил на одну вкладку было три. Магазин звал <see cref="Choice{T}"/>
        /// и добавлял жирность отдельной строкой; витрина скинов — то же, но
        /// жирность только включала и никогда не снимала; таблица лидеров
        /// красила все три свойства сама, и невыбранная вкладка у неё выходила
        /// ПРОЗРАЧНОЙ, а не приглушённой. Игрок ходит между разделами и видит,
        /// что одинаковые на вид ряды ведут себя по-разному.</para>
        ///
        /// <para>Жирность здесь не украшение: вкладка меняет и цвет, и вес — на
        /// солнце и на дешёвом экране цвета мало.</para>
        /// </summary>
        public static T Tab<T>(T el, bool active, float radius = -1f) where T : VisualElement
        {
            if (el == null) return null;
            Choice(el, active, radius);
            el.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            return el;
        }

        /// <summary>РОЛЬ С ЧУЖОЙ ПАЛИТРОЙ: новелла вправе переопределить цвета
        /// в манифесте (<c>accent_color</c>, <c>text_color</c>), и тогда экран
        /// приносит их сам — но собирает вид всё равно стилизатор, а не
        /// очередная копия четырёх строк.</summary>
        public static T Plate<T>(T el, Color plate, Color ink, float radius = -1f) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = plate;
            el.style.color = ink;
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, radius < 0f ? LvnTokens.Radius : radius);
            return el;
        }

        /// <summary>КНОПКА, ОДЕТАЯ АРТОМ: то же, что <see cref="Plate{T}"/>, но
        /// рамку роль НЕ трогает — под такой кнопкой может лежать 9-slice
        /// картинка новеллы, и рамка тогда часть её оформления, а не мусор
        /// от умолчаний.</summary>
        public static T Skinned<T>(T el, Color plate, Color ink, float radius) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = plate;
            el.style.color = ink;
            LvnChrome.Round(el, radius < 0f ? LvnTokens.Radius : radius);
            return el;
        }

        /// <summary>КАРТОЧКА или панель: поверхность темы со скруглением и
        /// кромкой, если тема её носит.</summary>
        public static T Card<T>(T el, float radius = -1f) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, radius < 0f ? LvnTokens.Radius : radius);
            LvnChrome.Edge(el);
            return el;
        }

        /// <summary>ПИЛЮЛЯ: приглушённая плашка, скруглённая под собственную
        /// высоту. Счётчики, метки, значения — всё, что читается ярлыком.</summary>
        public static T Pill<T>(T el, float height) where T : VisualElement
        {
            if (el == null) return null;
            el.style.height = height;
            el.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, height * 0.5f);
            return el;
        }

        /// <summary>ДОРОЖКА ШКАЛЫ — пустая часть полосы, обрезающая заливку по
        /// своим углам.
        ///
        /// <para><paramref name="tone"/> есть, потому что тонов дорожки в теме
        /// пока ДВА: загрузочные полосы берут <c>LvnTokens.Track</c>, а шкалы
        /// профиля и статов — <c>SurfaceHi</c>. Сводить их надо глазами на
        /// живом экране, а не вслепую, поэтому здесь пока сведена только
        /// механика (высота, скругление, обрезка), а тон звонящий приносит
        /// свой.</para></summary>
        public static T Track<T>(T el, float height, Color? tone = null) where T : VisualElement
        {
            if (el == null) return null;
            el.style.height = height;
            el.style.backgroundColor = tone ?? LvnTokens.Track;
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, height * 0.5f);
            el.style.overflow = Overflow.Hidden; // заливка обязана обрезаться по углам
            return el;
        }

        /// <summary>ЗАЛИВКА ШКАЛЫ — пройденная часть. По умолчанию акцент;
        /// <paramref name="tint"/> — для шкал со своим смыслом (золото за
        /// покупку, тревожный тон на исходе). Ширину ставит экран: это
        /// значение, а не роль.</summary>
        public static T Fill<T>(T el, float radius = -1f, Color? tint = null) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = tint ?? LvnTokens.Accent;
            LvnChrome.ClearBorder(el);
            LvnChrome.Round(el, radius < 0f ? LvnTokens.RadiusSm : radius);
            return el;
        }
    }
}
