using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ОГРАНКА ТЕМЫ, доступная любому экрану.
    ///
    /// <para>Цвета до экранов доезжают сами: <see cref="LvnTokens"/> — окно в
    /// действующую тему, и все триста обращений к нему стали темозависимыми.
    /// А вот огранка сама никуда не доедет — кромку по контуру, капс с
    /// разрядкой и фон за содержимым надо ПОСТАВИТЬ. Пока это умел только хаб,
    /// приложение выглядело перекрашенным, но не переодетым: хаб гранёный, а
    /// экран за ним — обычный тёмный список с новым цветом полосок.</para>
    ///
    /// <para>Здесь эти три действия лежат по одной строке на экран. Тема без
    /// огранки (midnight) не делает ничего, поэтому вызывать можно безусловно —
    /// и это важнее краткости: приём, который надо оборачивать в «если», рано
    /// или поздно забудут обернуть.</para>
    /// </summary>
    public static class LvnChrome
    {
        /// <summary>Мягкость кромки карточки. Число стояло восемью копиями —
        /// одинаковое, но безымянное, и «чуть светлее» пришлось бы править
        /// восемь раз, надеясь не пропустить девятую.</summary>
        public const float CardBorderStrength = 0.64f;

        /// <summary>
        /// КАРТОЧКА — поверхность, мягкая кромка, скруглённый угол. Три
        /// действия, которые в оболочке всегда идут вместе: строка профиля,
        /// плитка достижения, пак в магазине, ряд настроек.
        ///
        /// <para>Фон и радиус остаются доводкой вызывающего (выделенный пак
        /// светлее, крупная карточка круглее) — но по умолчанию берутся у
        /// темы, и тогда карточка везде одна и та же.</para>
        /// </summary>
        public static T Card<T>(T el, Color? bg = null, float? radius = null) where T : VisualElement
        {
            if (el == null) return el;
            el.style.backgroundColor = bg ?? LvnTokens.Surface;
            BorderSoft(el, CardBorderStrength);
            Round(el, radius ?? LvnTokens.RadiusSm);
            return el;
        }

        /// <summary>
        /// ЗАТЕМНЕНИЕ, ПО КОТОРОМУ ЗАКРЫВАЮТ, — вторая половина всплывающего
        /// листа: фон под ним и правило «тап мимо листа закрывает».
        ///
        /// <para>Правило было написано семью экранами и ДВУМЯ РАЗНЫМИ
        /// событиями: четыре закрывались по клику (нажал и отпустил на
        /// затемнении), два — по нажатию, то есть в момент касания. Разница
        /// видна пальцем: с нажатием случайный промах при перетаскивании
        /// закрывает панель, не дав отвести палец обратно. Правильное здесь —
        /// клик: закрывает намеренный тап, а не всякое касание.</para>
        ///
        /// <para>Тап ВНУТРИ листа сюда не доходит — за это отвечает
        /// <see cref="Sheet{T}"/>, ставящий на панель заслон.</para>
        /// </summary>
        public static T Scrim<T>(T root, System.Action onDismiss, Color? color = null) where T : VisualElement
        {
            if (root == null) return root;
            root.style.backgroundColor = color ?? LvnTokens.Scrim;
            if (onDismiss != null)
                root.RegisterCallback<ClickEvent>(e => { if (e.target == root) onDismiss(); });
            return root;
        }

        /// <summary>Отступ всплывающей панели от боков экрана — доля ширины.
        /// Число было написано пятью экранами по отдельности, причём двумя
        /// записями сразу (<c>Percent(8f)</c> и <c>Percent(8)</c>): менять
        /// «поля приложения» пришлось бы искать глазами.</summary>
        public const float SheetSidePercent = 8f;

        /// <summary>
        /// ЛИСТ ПОВЕРХ СЦЕНЫ — общая часть всякой всплывающей панели: она
        /// лежит абсолютно, отступает от боков на <see cref="SheetSidePercent"/>
        /// и НЕ ПРОПУСКАЕТ тап внутрь себя дальше — иначе нажатие на панель
        /// закрывает её же, потому что затемнение под ней слушает то же
        /// событие.
        ///
        /// <para>Высоту (сверху, снизу, потолок) и поля лист выбирает сам:
        /// у списка сохранений и у формы входа они разные по смыслу, и сводить
        /// их значило бы придумывать общее там, где его нет.</para>
        ///
        /// <para>Отступ бывает ДВУХ ЗАМЫСЛОВ, и оба живут через этот примитив:
        /// всплывающая панель отступает на 8% (<see cref="SheetSidePercent"/>),
        /// а лист накладного ЭКРАНА — на 5%, потому что он и есть почти весь
        /// экран. Два числа — два замысла; две РЕАЛИЗАЦИИ были лишними.</para>
        /// </summary>
        public static T Sheet<T>(T el, float sidePercent = SheetSidePercent) where T : VisualElement
        {
            if (el == null) return el;
            el.style.position = Position.Absolute;
            el.style.left = Length.Percent(sidePercent);
            el.style.right = Length.Percent(sidePercent);
            el.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            return el;
        }

        /// <summary>
        /// Скруглить все четыре угла. Мелочь — но именно она была скопирована
        /// в ТРИНАДЦАТЬ экранов слово в слово, а `ClearBorder` — в десять.
        /// Тринадцать копий одной строки означают тринадцать мест, куда надо
        /// зайти, чтобы поменять огранку темы, и одно из них всегда забывают.
        /// </summary>
        public static void Round(VisualElement el, float r)
        {
            if (el == null) return;
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        /// <summary>Скругление из темы: `Round(el)` вместо числа на глаз.</summary>
        public static void Round(VisualElement el) => Round(el, LvnTokens.Radius);

        /// <summary>
        /// КРУГ — ОДНО РЕШЕНИЕ, А НЕ ТРИ ЧИСЛА.
        ///
        /// <para>Круглых элементов в оболочке восемь — точка непрочитанного,
        /// кольцо аватара, медаль места, логотип, ореол под значком, ползунок
        /// переключателя, свотч цвета. Каждый собирался вручную: ширина,
        /// высота и скругление тремя отдельными строками, и связь между ними
        /// нигде не была записана.</para>
        ///
        /// <para>Хуже, что скругление брали ТОКЕНОМ ТЕМЫ, а круглыми они
        /// выходили по совпадению: свотч 56 px с <c>RadiusLg</c> = 28 — это
        /// ровно половина, но лишь потому, что два независимых числа темы
        /// сегодня так соотносятся. Логотип 50 px с тем же 28 круглый уже не
        /// поэтому, а потому что UITK зажимает радиус половиной коробки. Тема
        /// со своей огранкой (их две, и обе правят радиусы) молча превращает
        /// часть кругов в квадраты со скруглением — на живом экране это не
        /// «сломалось», а «стало чуть иначе», и заметить это некому.</para>
        ///
        /// <para>Здесь связь названа: диаметр один, радиус из него следует.
        /// Поменять размер и забыть скругление больше нельзя.</para>
        /// </summary>
        public static T Circle<T>(T el, float diameter) where T : VisualElement
        {
            if (el == null) return el;
            el.style.width = diameter;
            el.style.height = diameter;
            Round(el, diameter * 0.5f);
            return el;
        }

        /// <summary>
        /// ПИЛЮЛЯ — скругление по КОРОТКОЙ стороне, размер остаётся на
        /// вызывающем (полоса тянется флексом, её длину задаёт раскладка).
        ///
        /// <para>Отличается от круга ровно тем, что длину не трогает: дорожка
        /// прокрутки и её бегунок — одна ширина и разная высота.</para>
        /// </summary>
        public static T Pill<T>(T el, float thickness) where T : VisualElement
        {
            if (el == null) return el;
            Round(el, thickness * 0.5f);
            return el;
        }

        /// <summary>
        /// ОСТРЫЕ УГЛЫ — у картинки своя форма.
        ///
        /// <para>Ноль в скруглении значит не «ступень шкалы, равная нулю», а
        /// «скругления здесь нет вовсе»: под элемент подложили арт, который
        /// сам рисует свои края, и рамочное скругление срезало бы их поверх
        /// собственных. Написанное числом, это неотличимо от радиуса,
        /// подобранного на глаз, — и храповик радиусов считал его наравне.</para>
        /// </summary>
        public static void Sharp(VisualElement el) => Round(el, 0f);

        /// <summary>
        /// СКРУГЛИТЬ ТОЛЬКО ВЕРХ (или только низ).
        ///
        /// <para>Половинчатое скругление — не украшение, а следствие: картинка,
        /// вросшая в шапку карточки, обязана повторить скругление карточки
        /// СВОИМИ верхними углами, иначе её прямые углы вылезают за скруглённые
        /// углы карточки. То же снизу — для плашки, приросшей к нижнему краю.</para>
        ///
        /// <para>Правило стояло четырьмя написаниями: приватным помощником в
        /// окне диалога (видимым только ему) и тремя парами строк по месту — в
        /// витрине паков, на карточке хаба и в разметке слоя, где выписаны все
        /// четыре угла вместо <see cref="Round(VisualElement,float)"/>.</para>
        /// </summary>
        public static void RoundTop(VisualElement el, float r)
        {
            if (el == null) return;
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
        }

        /// <summary>Скруглить только низ — см. <see cref="RoundTop"/>.</summary>
        public static void RoundBottom(VisualElement el, float r)
        {
            if (el == null) return;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        /// <summary>
        /// ВО ВЕСЬ РОДИТЕЛЬ — абсолютная позиция и нули со всех четырёх сторон.
        ///
        /// <para>Самая частая пятистрочка интерфейса: фон, вуаль, скрим,
        /// подложка, ловушка нажатий — всё, что обязано накрыть родителя
        /// целиком. Дом для неё был (<c>ScreenUi.Stretch</c>, тридцать пять
        /// вызовов), но жил В ОБОЛОЧКЕ — а ядро сцены оболочку НЕ ВИДИТ по
        /// границам сборок. Девятнадцать мест писали пятёрку руками не по
        /// невнимательности: дотянуться было нечем.</para>
        ///
        /// <para>Урок шире этого метода: если дом зовут отовсюду, кроме одного
        /// слоя, — проверь не жильцов, а ЭТАЖ, на котором он стоит. Общая работа
        /// принадлежит нижнему слою; окно из верхнего остаётся для удобства.</para>
        /// </summary>
        public static T Stretch<T>(T el) where T : VisualElement
        {
            if (el == null) return null;
            el.style.position = Position.Absolute;
            el.style.left = 0;
            el.style.right = 0;
            el.style.top = 0;
            el.style.bottom = 0;
            return el;
        }

        /// <summary>ПОЛОСА ВДОЛЬ НИЖНЕГО КРАЯ — от края до края, прижата снизу.
        ///
        /// <para>Одиннадцать мест: навбар, подпись под кадром галереи, вуаль
        /// под текстом обложки, марка на бут-экране, плашка примерки. Все
        /// пишут одно и то же четырьмя строками, отличаясь лишь отступами.</para>
        ///
        /// <para>Опасна тут не длина, а ЧТО ИМЕННО забывают. Пропусти
        /// <c>Absolute</c> — полоса встанет в поток и раздвинет содержимое;
        /// пропусти <c>right</c> — схлопнется по ширине текста и прижмётся
        /// влево. Оба случая выглядят как «вёрстка поехала», и виноватым
        /// кажется родитель.</para>
        ///
        /// <para>Верх НЕ трогаем намеренно: высоту полосе задаёт содержимое
        /// (или сам вызывающий), и прижать её сразу к обоим краям значило бы
        /// растянуть на весь родитель — это уже <see cref="Stretch"/>.</para>
        /// </summary>
        public static T BottomStrip<T>(T el) where T : VisualElement
            => BottomStrip(el, 0f, 0f);

        /// <summary>То же с отступами. Тип <c>StyleLength</c>, а не число:
        /// подпись под кадром галереи отступает ДОЛЕЙ высоты, и заставлять её
        /// считать пиксели значило бы вернуть ей рукописную вёрстку.</summary>
        public static T BottomStrip<T>(T el, StyleLength side, StyleLength bottom) where T : VisualElement
        {
            if (el == null) return null;
            el.style.position = Position.Absolute;
            el.style.left = side;
            el.style.right = side;
            el.style.bottom = bottom;
            return el;
        }

        /// <summary>Огранка ПО ТЕМЕ: скругление и тонкая кромка акцентом.
        ///
        /// <para>Третья по частоте пара в оболочке (8 мест в восьми файлах):
        /// <c>Edge</c> и <c>Round</c> ходят вместе, потому что кромка без
        /// скругления обрезает угол, а скругление без кромки теряет тему.
        /// Это одно оформление, и звать его надо один раз.</para></summary>
        public static void Edged(VisualElement el, float r, float strength = 1f)
        {
            Round(el, r);
            Edge(el, strength);
        }

        /// <summary>ОГРАНКА ЦЕЛИКОМ: скругление и рамка одним решением.
        ///
        /// <para>Заведено 01.09 по замеру: пара «скруглить + обвести» стояла
        /// 22 раза в двадцати файлах, в обоих порядках, — вторая по частоте
        /// последовательность в оболочке. Радиус и рамка описывают ОДИН край
        /// прямоугольника; порознь их держали только потому, что дом умел
        /// каждое по отдельности и не умел вместе.</para></summary>
        public static void Frame(VisualElement el, float r, Color color, float width)
        {
            Round(el, r);
            Border(el, color, width);
        }

        /// <summary>Огранка без рамки: скруглить и снять обводку, если была.
        /// Нужна, когда элемент переодевают — прежняя рамка иначе остаётся.</summary>
        public static void Frame(VisualElement el, float r)
        {
            Round(el, r);
            ClearBorder(el);
        }

        /// <summary>
        /// ПЕРЕКРАСИТЬ РАМКУ, не трогая толщину — когда элемент уже обведён, а
        /// сменилось только состояние (выбран, куплен, активен).
        ///
        /// <para>НЕ ПОДКЛЮЧЁН с 01.09: оба экрана, ради которых роль заводили,
        /// с тех пор переведены на <see cref="Border"/>, где цвет и толщина
        /// идут вместе. Роль оставлена намеренно — «перекрасить, не трогая
        /// толщину» остаётся законным вопросом (состояние карточки меняется
        /// чаще, чем её огранка), и переписывать её заново дороже, чем
        /// держать. Ждёт первого места, где толщину менять НЕ надо.</para>
        ///
        /// <para>Копия этих четырёх строк жила приватно в двух экранах разом
        /// (<c>SetBorderColor</c> в ленте хаба и в ежедневной награде) — ровно
        /// половина <see cref="Border"/>, вырезанная потому, что толщину в тот
        /// момент менять было не нужно. Дом отдавал работу целиком или никак, и
        /// каждый, кому нужна была половина, отрезал её себе сам.</para>
        /// </summary>
        public static void Tint(VisualElement el, Color color)
        {
            if (el == null) return;
            el.style.borderTopColor = color;
            el.style.borderBottomColor = color;
            el.style.borderLeftColor = color;
            el.style.borderRightColor = color;
        }

        /// <summary>Снять рамку со всех четырёх сторон.</summary>
        public static void ClearBorder(VisualElement el)
        {
            if (el == null) return;
            el.style.borderTopWidth = 0;
            el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0;
            el.style.borderRightWidth = 0;
        }

        /// <summary>
        /// Поле ввода в тонах темы. Единственное место в игре, которое до сих
        /// пор выглядело отладочной формой: белая полоса с серой кнопкой
        /// посреди тёмной сцены.
        ///
        /// <para>Красить приходится ВНУТРЕННИЙ элемент поля, а не само поле:
        /// у TextField своя подложка, и цвет, поставленный снаружи, до неё не
        /// доходит — именно поэтому «покрасил, а оно белое».</para>
        /// </summary>
        public static void Field(TextField f, Color bg, Color text)
        {
            if (f == null) return;
            f.style.color = text;
            var input = f.Q(TextField.textInputUssName);
            if (input == null) return;
            input.style.backgroundColor = bg;
            input.style.color = text;
            LvnAir.Pad(input, LvnTokens.Space3, LvnTokens.Space2);
        }

        /// <summary>
        /// КРОМКА С ОДНОЙ СТОРОНЫ — толщина и цвет вместе.
        ///
        /// <para>Односторонняя кромка в оболочке работает тремя разными
        /// ролями: волосяной РАЗДЕЛИТЕЛЬ (низ верхнего бара, верх подвала),
        /// акцентная ПОЛОСКА-маркер слева («это ты» в таблице лидеров,
        /// «качается сейчас» в очереди) и КРЫШКА сверху. Роли разные, механизм
        /// один — и он был написан по месту девять раз парами строк, где
        /// толщина и цвет стоят порознь и разъезжаются поодиночке.</para>
        ///
        /// <para>Пара — не украшение: кромка нулевой толщины невидима любого
        /// цвета, а цветная без толщины не рисуется вовсе. Порознь они значат
        /// «полработы», и именно так и терялись.</para>
        /// </summary>
        public static void EdgeOn(VisualElement el, LvnSide side, Color color, float width)
        {
            if (el == null) return;
            switch (side)
            {
                case LvnSide.Top:
                    el.style.borderTopWidth = width; el.style.borderTopColor = color; break;
                case LvnSide.Bottom:
                    el.style.borderBottomWidth = width; el.style.borderBottomColor = color; break;
                case LvnSide.Left:
                    el.style.borderLeftWidth = width; el.style.borderLeftColor = color; break;
                case LvnSide.Right:
                    el.style.borderRightWidth = width; el.style.borderRightColor = color; break;
            }
        }

        /// <summary>РАЗДЕЛИТЕЛЬ — волосяная линия темы, отделяющая полосу от
        /// того, что под ней. Толщина и тон у неё не «выбраны здесь», а общие:
        /// разъехавшись, они читаются как небрежность вёрстки.</summary>
        public static void Divider(VisualElement el, LvnSide side = LvnSide.Bottom, float width = 1f)
            => EdgeOn(el, side, LvnTokens.Border, width);

        /// <summary>ПОЛОСКА-МАРКЕР слева: кромка работает знаком состояния —
        /// «эта строка твоя», «эта глава качается». Остальные три стороны
        /// гасятся: полоска значит «только слева», и строка, доставшаяся от
        /// прежнего состояния, не должна принести с собой чужую рамку.</summary>
        public static void Stripe(VisualElement el, Color? color = null, float width = 3f)
        {
            if (el == null) return;
            Border(el, Color.clear, 0f);
            EdgeOn(el, LvnSide.Left, color ?? LvnTokens.Accent, width);
        }

        /// <summary>Ровная рамка одного цвета и толщины по всем сторонам.</summary>
        public static void Border(VisualElement el, Color color, float width)
        {
            if (el == null) return;
            el.style.borderTopWidth = width;
            el.style.borderBottomWidth = width;
            el.style.borderLeftWidth = width;
            el.style.borderRightWidth = width;
            // Цвет ставит РОЛЬ «перекрасить»: обводка — это толщина плюс цвет,
            // и цветную половину незачем писать здесь второй раз. Заодно роль
            // перестала быть мёртвой дверью: она ждала первого места, где
            // толщину менять не надо, — а нашлась в месте, где надо и то и
            // другое, и половина всё равно её.
            Tint(el, color);
        }

        /// <summary>
        /// КРОМКА РАМКИ ВПОЛСИЛЫ — цвет берёт тема, плотность называет экран.
        ///
        /// <para>«Обвести доп-цветом темы, но потише» — самая частая огранка
        /// оболочки: карточки, строки списка, плитки. Записывалась она восемь
        /// раз одинаковой пятёрней (взять токен, разобрать на составляющие,
        /// собрать обратно с умноженной прозрачностью), и в этой пятёрне
        /// терялось единственное, что различает места, — САМА ДОЛЯ. Она молча
        /// разъехалась: одна и та же роль обведена то на 0.55, то на 0.64, то
        /// на 0.7, и заметить это можно было только сложив четыре файла рядом.
        /// Теперь доля стоит в вызове одним числом.</para>
        ///
        /// <para>Умножение, а не замена: кромка обязана слушаться темы. Тема с
        /// прозрачной рамкой (её выбирает новелла) не должна получить видимую
        /// обводку оттого, что экран попросил «вполсилы».</para>
        /// </summary>
        public static void BorderSoft(VisualElement el, float strength, float width = 1f)
        {
            if (el == null) return;
            Border(el, BorderTone(strength), width);
        }

        /// <summary>Тот же цвет отдельно — тем, кто красит НЕ ВСЕ четыре
        /// стороны: у выделенной карточки верх акцентный, а остальные три
        /// обязаны остаться тихими и теми же.</summary>
        public static Color BorderTone(float strength)
        {
            var c = LvnTokens.Border;
            return UiColor.WithAlpha(c, c.a * strength);
        }

        /// <summary>
        /// КРЫШКА: акцентная кромка СВЕРХУ и только сверху.
        ///
        /// <para>Даёт листу край, которого не хватало на тёмном полотне: без неё
        /// всплывающая панель сливается с фоном, и непонятно, где она
        /// начинается. Стояла двумя копиями — у листа накладного экрана и у
        /// панели вкладки гардероба, — с одинаковой толщиной, набранной
        /// числом.</para>
        /// </summary>
        public static void Lid(VisualElement el, float width = 2.5f)
        {
            EdgeOn(el, LvnSide.Top, LvnTokens.Accent, width);
        }

        /// <summary>Светящаяся кромка по контуру. <paramref name="strength"/> —
        /// доля от штатной яркости: у крупного кадра она может быть тише, чем у
        /// мелкой плашки, иначе кадр читается как обведённый маркером.</summary>
        public static void Edge(VisualElement el, float strength = 1f)
        {
            var t = LvnTheme.Current;
            if (el == null || t.EdgeWidth <= 0f) return;
            float w = t.EdgeWidth;
            el.style.borderTopWidth = w; el.style.borderBottomWidth = w;
            el.style.borderLeftWidth = w; el.style.borderRightWidth = w;
            var c = UiColor.WithAlpha(t.Accent, t.EdgeAlpha * strength);
            el.style.borderTopColor = c; el.style.borderBottomColor = c;
            el.style.borderLeftColor = c; el.style.borderRightColor = c;
        }

        /// <summary>Фон темы под содержимое экрана. Зовётся сразу после того,
        /// как выставлен цвет фона, и ДО того, как добавлено содержимое.</summary>
        public static void Backdrop(VisualElement root) => LvnBackdrop.Apply(root, LvnTheme.Current);

        /// <summary>Заголовок по теме: капс и разрядка там, где тема их
        /// требует. Возвращает тот же элемент, чтобы вставать в цепочку.</summary>
        public static Label Heading(Label l)
        {
            var t = LvnTheme.Current;
            if (l == null) return null;
            if (t.UpperHeadings && !string.IsNullOrEmpty(l.text)) l.text = l.text.ToUpperInvariant();
            l.style.letterSpacing = t.Tracking;
            return l;
        }

        /// <summary>
        /// Накладывает рамку темы на панель диалога.
        ///
        /// <para>Рамка идёт ОТДЕЛЬНЫМ слоем, а не фоном самой панели. Причина
        /// в том, что у неё прозрачная середина: сделай её фоном — и текст
        /// ляжет на просвечивающий задник, где его не прочитать. Слой поверх
        /// заливки оставляет обе вещи независимыми: заливка отвечает за
        /// читаемость, рамка — за вид.</para>
        ///
        /// <para>Слой уходит в самый низ списка детей: он должен быть НАД
        /// заливкой панели, но ПОД её текстом.</para>
        /// </summary>
        public static void Frame(VisualElement panel)
        {
            var t = LvnTheme.Current;
            if (panel == null || string.IsNullOrEmpty(t.DialogueFrame)) return;
            var tex = Resources.Load<Texture2D>("ui/" + t.DialogueFrame);
            if (tex == null) return;   // нет файла — панель остаётся как была

            var f = new VisualElement { name = "lvn-frame", pickingMode = PickingMode.Ignore };
            f.style.position = Position.Absolute;
            // Выступ наружу: линия рамки нарисована внутри своего файла, и без
            // него заливка панели торчала бы из-под неё по всему периметру.
            float bleed = t.DialogueFrameBleed;
            f.style.left = -bleed; f.style.right = -bleed;
            f.style.top = -bleed; f.style.bottom = -bleed;
            f.style.backgroundImage = new StyleBackground(tex);
            LvnPicture.Slice(f, t.DialogueFrameSlice, t.DialogueFrameScale);
            panel.Add(f);
            f.SendToBack();
        }

        /// <summary>
        /// Плашка имени говорящего — картинкой из темы.
        ///
        /// <para>В отличие от окна, здесь картинка идёт ФОНОМ, а не слоем
        /// поверх: у плашки середина непрозрачная, и текст на ней читается.
        /// Отступы берутся из темы, потому что у рисованной плашки поля свои —
        /// снизу кромка прямая, сверху накладная пластина.</para>
        /// </summary>
        public static bool Bubble(VisualElement plate)
        {
            var t = LvnTheme.Current;
            if (plate == null || string.IsNullOrEmpty(t.SpeakerBubble)) return false;
            var tex = Resources.Load<Texture2D>("ui/" + t.SpeakerBubble);
            if (tex == null) return false;

            plate.style.backgroundImage = new StyleBackground(tex);
            plate.style.backgroundColor = Color.clear;   // иначе из-под срезанных углов торчит прямоугольник
            LvnPicture.Slice(plate, t.SpeakerBubbleSlice, t.DialogueFrameScale);
            // Своя форма у картинки — скругление рамки снимается, иначе она
            // срежет углы арта поверх его собственных.
            Sharp(plate);
            plate.style.paddingLeft = t.SpeakerBubblePad.x;
            plate.style.paddingRight = t.SpeakerBubblePad.y;
            plate.style.paddingTop = t.SpeakerBubblePad.z;
            plate.style.paddingBottom = t.SpeakerBubblePad.w;
            plate.style.marginLeft = t.SpeakerBubbleOffsetX;
            return true;
        }
    }
}
