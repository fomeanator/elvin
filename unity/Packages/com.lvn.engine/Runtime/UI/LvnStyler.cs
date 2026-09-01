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

        /// <summary>
        /// ГНЕЗДО ПОД ЗНАК — квадратная кнопка, внутри которой только иконка или
        /// один символ: «назад», «закрыть», стрелки листания, шестерёнка.
        ///
        /// <para>Собиралась вручную в пяти местах по десять строк: обнулить все
        /// четыре отступа (иначе символ уезжает из центра), выровнять по обеим
        /// осям, задать квадрат, положить тихую плашку, снять рамку, скруглить.
        /// Ни одна строка не выглядит лишней — и именно поэтому их переписывали
        /// заново каждый раз, а не искали общее.</para>
        ///
        /// <para>Разъехалось всё, что могло: размеры 44, 46, 52, 56 и 60 —
        /// подобранные каждый на своём экране; плашка то <c>Faint</c> темы, то
        /// белая с прозрачностью 0.12 мимо палитры. Кнопка «назад» в галерее и
        /// «назад» в таблице лидеров — один и тот же жест, а на глаз это разные
        /// кнопки.</para>
        ///
        /// <para>РАЗМЕР остаётся у вызывающего: над постером во весь экран
        /// уместна кнопка крупнее, чем в плотной шапке списка. Разнобой был не в
        /// размере, а в том, что каждый заново решал ВСЁ ОСТАЛЬНОЕ.</para>
        /// </summary>
        public static T IconSlot<T>(T el, float size, float radius = -1f) where T : VisualElement
        {
            if (el == null) return null;
            el.style.width = size;
            el.style.height = size;
            LvnAir.Pad(el, 0);
            el.style.alignItems = Align.Center;
            el.style.justifyContent = Justify.Center;
            return Plate(el, LvnTokens.Faint, LvnTokens.Text,
                         radius < 0f ? LvnTokens.RadiusSm : radius);
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
            LvnChrome.Frame(el, radius < 0f ? LvnTokens.Radius : radius);
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
            LvnChrome.Frame(el, radius < 0f ? LvnTokens.Radius : radius);
            LvnChrome.Edge(el);
            return el;
        }

        /// <summary>
        /// СТРОКА СПИСКА: плитка во всю ширину, содержимое в ряд по центру,
        /// поверхность темы, малое скругление и вертикальный воздух.
        ///
        /// <para>Ею набраны списки глав, сейвов, наград и достижений. Поля
        /// СНАРУЖИ (отступ до соседней строки) и по горизонтали остаются
        /// экрану: они про его поля, а не про саму строку, и у разных списков
        /// честно разные.</para>
        /// </summary>
        public static T ListRow<T>(T el, Color? fill = null) where T : VisualElement
        {
            if (el == null) return null;
            el.style.flexShrink = 0;
            el.style.flexDirection = FlexDirection.Row;
            el.style.alignItems = Align.Center;
            el.style.backgroundColor = fill ?? LvnTokens.Surface;
            LvnChrome.Round(el, LvnTokens.RadiusSm);
            LvnAir.PadY(el, LvnTokens.Space2);
            return el;
        }

        /// <summary>
        /// СТРОКА-КАРТОЧКА: та же строка списка, но с мягкой кромкой.
        ///
        /// <para>Ролей две, потому что разница ЕСТЬ и она осмысленная: строки
        /// глав и сейвов идут плотным списком, где кромка у каждой давала бы
        /// сетку; строки профиля стоят вразбивку, и кромка отделяет их от
        /// пустого фона. Слить их в одну роль значило бы принять решение за
        /// художника, а не за уборщика.</para>
        ///
        /// <para><b>Дом строки списка был жив, проверен и почти никому не
        /// известен.</b> Его звали два экрана из шести: остальные четыре
        /// собирали ту же плитку руками — <c>Card</c>, отступ, скругление, — и
        /// половина из них при этом не знала, что у неё получается кромка, а у
        /// соседа нет. Это не отсутствие дома, а незнание о нём: находится
        /// только тем, что ищут не «где нет дома», а «где его не позвали».</para>
        /// </summary>
        public static T CardRow<T>(T el, Color? fill = null) where T : VisualElement
        {
            if (el == null) return null;
            ListRow(el, fill);
            LvnChrome.BorderSoft(el, LvnChrome.CardBorderStrength);
            return el;
        }

        /// <summary>
        /// ПАНЕЛЬ: лист, всплывающий поверх экрана. Знакомство, выбор сервера,
        /// окно статов, меню сцены.
        ///
        /// <para>Положение даёт <see cref="LvnChrome.Sheet{T}"/> — он и был
        /// общим. А ОДЕВАЛСЯ лист в каждом экране заново, четырьмя строками
        /// подряд: заливка, отступ вбок, отступ вверх, скругление. Четыре
        /// панели — четыре набора чисел, и ни одно из различий нигде не
        /// объяснено.</para>
        ///
        /// <para>Сводить их я не стал: как выглядит панель — вопрос
        /// художника. Сделано другое — различия видны в самом вызове, а форма
        /// названа один раз. То же решение, что у ярлыка, и по той же
        /// причине: расхождение, которое нельзя увидеть, не обсуждается.</para>
        ///
        /// <para>Отступы здесь тоже часть роли: лист без воздуха внутри — не
        /// лист, а прямоугольник с текстом впритык к краю.</para>
        /// </summary>
        public static T Panel<T>(T el, Color fill, float radius = -1f,
                                 float padX = -1f, float padY = -1f) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = fill;
            LvnAir.Pad(el, padX < 0f ? LvnTokens.Space4 : padX, padY < 0f ? LvnTokens.Space4 : padY);
            LvnChrome.Round(el, radius < 0f ? LvnTokens.Radius : radius);
            return el;
        }

        /// <summary>
        /// ЯРЛЫК: тесная плашка с надписью или значком внутри. Цена, состав
        /// набора, жанр новеллы, счётчик галереи, значок достижения — всё, что
        /// читается коротким словом на фоне.
        ///
        /// <para><b>Их было пять, и правил тоже пять.</b> Заливка, оба отступа,
        /// скругление и рамка — каждый экран решал заново, и ни одно из решений
        /// нигде не записано. Дошло до противоположных: у значка в профиле
        /// боковой отступ МЕНЬШЕ вертикального, у всех остальных — больше.
        /// Разницу нельзя было увидеть, не открыв пять файлов.</para>
        ///
        /// <para>Здесь она видна с одного взгляда: у кого что отличается,
        /// написано в самом вызове. Сводить их — решение художника, а не
        /// уборщика, и теперь для него есть одно место.</para>
        ///
        /// <para>Отступы попадают в роль, хотя стилизатор их обычно не трогает:
        /// у ярлыка теснота КОНСТИТУТИВНА — плашка, облегающая слово, и есть
        /// ярлык. Это исключение, и оно названо, чтобы не расползлось.</para>
        /// </summary>
        public static T Chip<T>(T el, Color fill, float radius = -1f, Color? edge = null,
                                float padX = -1f, float padY = -1f) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = fill;
            LvnAir.Pad(el, padX < 0f ? LvnTokens.Space2 : padX, padY < 0f ? LvnTokens.Tight : padY);
            if (edge is Color e) LvnChrome.Frame(el, radius < 0f ? LvnTokens.RadiusSm : radius, e, 1f);
            return el;
        }

        /// <summary>ПИЛЮЛЯ: приглушённая плашка, скруглённая под собственную
        /// высоту. Счётчики, метки, значения — всё, что читается ярлыком.</summary>
        public static T Pill<T>(T el, float height) where T : VisualElement
        {
            if (el == null) return null;
            el.style.height = height;
            el.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Frame(el, height * 0.5f);
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
            LvnChrome.Frame(el, height * 0.5f);
            el.style.overflow = Overflow.Hidden; // заливка обязана обрезаться по углам
            return el;
        }

        /// <summary>ШКАЛА ЦЕЛИКОМ: дорожка с заливкой внутри.
        ///
        /// <para>Собиралась вручную в трёх экранах и в слое живых значений —
        /// четыре копии одной вёрстки. Копии уже разъехались: профиль писал
        /// высоту заливки ЧИСЛОМ (<c>fill.style.height = 16</c>), статы —
        /// процентом; при смене высоты дорожки первый вариант молча оставил бы
        /// заливку прежней.</para>
        ///
        /// <para>Здесь же названы два правила, которые вызывающие держали в
        /// уме: <b>радиус заливки — половина высоты</b> (иначе её углы не
        /// совпадают с дорожкой) и <b>заливка занимает дорожку по высоте</b>.
        /// Оба выводятся из одной величины, и делить её пополам вручную больше
        /// не нужно.</para>
        ///
        /// <para>Заливка — первый ребёнок дорожки: на это уже опирается слой
        /// живых значений, и <see cref="BarSet"/> обновляет её оттуда же.</para>
        /// </summary>
        public static VisualElement Bar(float height, float frac = 0f,
                                        Color? tone = null, Color? tint = null)
        {
            var track = Track(new VisualElement(), height, tone);
            var fill = Fill(new VisualElement(), height * 0.5f, tint);
            fill.style.height = Length.Percent(100f);
            track.Add(fill);
            BarSet(track, frac);
            return track;
        }

        /// <summary>Подвинуть шкалу: заливка — первый ребёнок дорожки.</summary>
        public static void BarSet(VisualElement track, float frac)
        {
            if (track == null || track.childCount == 0) return;
            FillTo(track[0], frac);
        }

        /// <summary>ПОЛОСА РАСТЁТ, А НЕ ПЕРЕПРЫГИВАЕТ.
        ///
        /// <para>Заполнение ставили присваиванием ширины: данные приходят раз в
        /// треть секунды, и полоса дёргалась ступеньками — на глаз это читается
        /// как подвисание, а не как ход. Здесь она доезжает до новой доли за
        /// один короткий ход, поэтому движение непрерывно даже на редких
        /// данных.</para>
        ///
        /// <para>Назад — БЕЗ анимации: откат (сменилась глава, пересчитали
        /// знаменатель) не событие для глаза, а поправка учёта; ползти назад
        /// значило бы показывать несуществующее «разгружается».</para>
        ///
        /// <para>Правило жило в оболочке (<c>ScreenUi.SetFill</c>), пока шкалу
        /// собирали руками в четырёх местах. Оно принадлежит шкале: экран,
        /// который её показывает, не обязан знать, как она ходит.</para>
        /// </summary>
        public static void FillTo(VisualElement fill, float frac)
        {
            if (fill == null) return;
            frac = Mathf.Clamp01(frac);
            // Откуда ехать: у ВЫСТАВЛЕННОЙ доли ключевое слово Undefined (Null
            // значит «свойство не трогали»). Перепутать их значит каждый раз
            // начинать ход от нуля — полоса дёргалась бы к началу на каждом
            // обновлении.
            var w = fill.style.width;
            float now = w.keyword == StyleKeyword.Undefined && w.value.unit == LengthUnit.Percent
                ? Mathf.Clamp01(w.value.value / 100f) : 0f;
            if (frac <= now + 0.0005f)   // назад и на месте — сразу
            {
                fill.style.width = new Length(frac * 100f, LengthUnit.Percent);
                return;
            }
            fill.experimental.animation.Start(now, frac, LvnMotion.Ms(LvnMotion.Calm),
                (e, v) => e.style.width = new Length(v * 100f, LengthUnit.Percent));
        }

        /// <summary>ЗАЛИВКА ШКАЛЫ — пройденная часть. По умолчанию акцент;
        /// <paramref name="tint"/> — для шкал со своим смыслом (золото за
        /// покупку, тревожный тон на исходе). Ширину ставит экран: это
        /// значение, а не роль.</summary>
        public static T Fill<T>(T el, float radius = -1f, Color? tint = null) where T : VisualElement
        {
            if (el == null) return null;
            el.style.backgroundColor = tint ?? LvnTokens.Accent;
            LvnChrome.Frame(el, radius < 0f ? LvnTokens.RadiusSm : radius);
            return el;
        }
    }
}
