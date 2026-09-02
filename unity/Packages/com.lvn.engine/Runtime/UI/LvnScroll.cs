using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ДЛИННЫЙ СПИСОК — как он выглядит и как его тянут.
    ///
    /// <para>Списков в оболочке два десятка: настройки, профиль, магазин,
    /// карточка новеллы, галерея, лента нарядов, колонка лиц. Каждый заводился
    /// вручную, и правила разошлись: где-то полоса прокрутки видна, где-то
    /// скрыта, где-то скрыта только горизонтальная. Читается это как разные
    /// экраны разных приложений.</para>
    ///
    /// <para>Хуже другое. UITK тянет список колесом и тач-жестом, но не
    /// перетаскиванием указателя — на десктопе и в редакторе список стоит
    /// мёртво, если полосу не тронуть. Обход написали один раз, приватно
    /// внутри гардероба (Илья 28.08: «надо их скролабл сделать»), и достался
    /// он ровно одному списку из двадцати двух.</para>
    ///
    /// <para>Обход неочевидный, и в нём две пойманные живьём ловушки: захват
    /// указателя отдаётся списку только ПОСЛЕ порога в 8 пикселей (иначе тап по
    /// карточке перестаёт работать), и жест обрывается, если кнопка уже
    /// отпущена, а событие до нас не дошло — так бывает, когда обработчик тапа
    /// пересобирает список и уносит цель события с собой; без проверки список
    /// потом ехал за курсором без нажатия.</para>
    /// </summary>
    public static class LvnScroll
    {
        /// <summary>Порог, после которого жест признаётся прокруткой, а не
        /// тапом. Меньше — и список уезжает от дрожи пальца на кнопке.</summary>
        public const float DragThreshold = 8f;

        /// <summary>
        /// ПЕРЕСОБРАТЬ СПИСОК, НЕ ТЕРЯЯ МЕСТА, НА КОТОРОМ СТОИТ ИГРОК.
        ///
        /// <para><c>Clear()</c> обнуляет содержимое, а вместе с ним и
        /// прокрутку: высота падает до нуля, и <c>scrollOffset</c> зажимается
        /// в начало. Новые дети приходят уже к нулю — список прыгает наверх.
        /// Видно это не всегда: пересобирают обычно при открытии, когда игрок
        /// и так в начале. Но там, где список пересобирают НА ГЛАЗАХ (панель
        /// загрузок обновляется, когда глава встала в очередь или доехала),
        /// прыжок настоящий.</para>
        ///
        /// <para>Место возвращается дважды: сразу — на случай, если раскладка
        /// уцелела, — и на первой пересчитанной геометрии, потому что до неё
        /// возвращать некуда. Страховка снимает подписку, чтобы список,
        /// который больше не меняет размер, не таскал её вечно.</para>
        ///
        /// <para>Стоял в начале — ничего не делаем: возвращать нечего, а лишняя
        /// подписка на геометрию стоит дороже.</para>
        /// </summary>
        public static void Keeping(ScrollView view, System.Action rebuild)
        {
            if (view == null) { rebuild?.Invoke(); return; }
            var was = view.scrollOffset;
            rebuild?.Invoke();
            if (was.sqrMagnitude <= 0.0001f) return;

            view.scrollOffset = was;
            EventCallback<GeometryChangedEvent> back = null;
            back = _ =>
            {
                view.scrollOffset = was;
                view.contentContainer.UnregisterCallback(back);
            };
            view.contentContainer.RegisterCallback(back);
            // Страховка ВОЗВРАЩАЕТ, а не просто отписывает: у пустого тела
            // геометрию считать не на чем, события может не быть вовсе — и
            // отписка без возврата оставила бы игрока в начале молча.
            view.schedule.Execute(() =>
            {
                view.scrollOffset = was;
                view.contentContainer.UnregisterCallback(back);
            }).ExecuteLater(96);
        }

        /// <summary>Вертикальный список с общими правилами: полос нет (на
        /// телефоне их всё равно не хватают пальцем), тянется рукой.</summary>
        public static ScrollView Vertical(bool showScroller = false)
        {
            var sv = new ScrollView(ScrollViewMode.Vertical);
            sv.verticalScrollerVisibility = showScroller ? ScrollerVisibility.Auto : ScrollerVisibility.Hidden;
            sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            DragToScroll(sv);
            return sv;
        }

        /// <summary>Горизонтальная лента: та же рука, ряд вместо колонки.</summary>
        public static ScrollView Horizontal()
        {
            var sv = new ScrollView(ScrollViewMode.Horizontal);
            sv.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            sv.contentContainer.style.flexDirection = FlexDirection.Row;
            DragToScroll(sv);
            return sv;
        }

        /// <summary>Научить готовый список тянуться указателем. Повторный вызов
        /// на том же списке безвреден — обработчики ставятся один раз.</summary>
        public static void DragToScroll(ScrollView sv)
        {
            if (sv == null || Taught.TryGetValue(sv, out _)) return;
            Taught.Add(sv, Marker);   // метка «этот уже умеет»: экраны зовут дважды

            bool down = false, dragging = false;
            int pid = -1;
            Vector2 startPos = default, startOff = default;
            bool horizontal = sv.mode == ScrollViewMode.Horizontal;

            sv.RegisterCallback<PointerDownEvent>(e =>
            {
                down = true; dragging = false; pid = e.pointerId;
                startPos = e.position; startOff = sv.scrollOffset;
            }, TrickleDown.TrickleDown);

            void EndGesture()
            {
                if (pid != -1 && sv.HasPointerCapture(pid)) sv.ReleasePointer(pid);
                down = false; dragging = false; pid = -1;
            }

            sv.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!down || e.pointerId != pid) return;
                // Кнопка уже отпущена, а PointerUp до нас не дошёл. Это штатно:
                // тап по карточке пересобирает список, элемент под курсором
                // исчезает — и событие отпускания уходит вместе с ним. Без этой
                // проверки жест оставался «нажатым» навсегда, и список ехал за
                // курсором без нажатия (Илья 26.08).
                if (e.pressedButtons == 0) { EndGesture(); return; }
                var d = (Vector2)e.position - startPos;
                float move = horizontal ? d.x : d.y;
                if (!dragging && Mathf.Abs(move) > DragThreshold)
                {
                    // Захват забираем ТОЛЬКО после порога: до него нажатие
                    // принадлежит карточке, и тап без движения работает как
                    // работал. После — карточка получает CaptureOut и клика не
                    // будет: палец прокручивал, а не выбирал.
                    dragging = true;
                    sv.CapturePointer(pid);
                }
                if (dragging)
                    sv.scrollOffset = horizontal
                        ? new Vector2(startOff.x - d.x, startOff.y)
                        : new Vector2(startOff.x, startOff.y - d.y);
            });

            // TrickleDown: отпускание должно дойти до нас ДО того, как обработчик
            // карточки пересоберёт список и заберёт с собой цель события.
            sv.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.pointerId == pid) EndGesture();
            }, TrickleDown.TrickleDown);

            // Захват потерян не нами (перестройка, другой элемент) — жест мёртв.
            sv.RegisterCallback<PointerCaptureOutEvent>(_ => { down = false; dragging = false; pid = -1; });
        }

        // Метка «список уже умеет тянуться» — в таблице со слабыми ключами, а
        // не в el.userData: карман элемента принадлежит экрану (id вкладки, url
        // арта), и служебные пометки движка в нём затирают чужое.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollView, object>
            Taught = new System.Runtime.CompilerServices.ConditionalWeakTable<ScrollView, object>();
        private static readonly object Marker = new object();
    }
}
