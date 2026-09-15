using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ТОНКАЯ ПОЛОСА ПРОКРУТКИ — дорожка сегментами и бегунок у правого края
    /// (TR-121). Родилась в колонке эмоций гардероба; Илья 15.09: «в
    /// настройках сделать прокрут как на эмоциях в гардеробе, модным». Один
    /// дом на все списки: штатный скроллер UITK толстый и серый, а эта полоса
    /// читается частью оформления. Сама прячется, когда всё влезает, и
    /// следит за списком сама — хозяин только ставит её на место.
    /// </summary>
    public sealed class LvnSlimScroll : VisualElement
    {
        /// <summary>Ширина дорожки.</summary>
        public const float Width = 6f;
        private const int Segments = 4;
        private const float ThumbMin = 26f;

        private readonly ScrollView _view;
        private readonly VisualElement _thumb;

        public LvnSlimScroll(ScrollView view)
        {
            _view = view;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.width = Width;
            style.display = DisplayStyle.None;
            for (int s = 0; s < Segments; s++)
            {
                var seg = new VisualElement { pickingMode = PickingMode.Ignore };
                seg.style.flexGrow = 1;
                seg.style.marginBottom = s == Segments - 1 ? 0 : 4;
                seg.style.backgroundColor = LvnTokens.Track;
                LvnChrome.Pill(seg, Width);
                Add(seg);
            }
            _thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            _thumb.style.position = Position.Absolute;
            _thumb.style.left = 0; _thumb.style.right = 0;
            _thumb.style.backgroundColor = UiColor.WithAlpha(Color.white, 0.62f);
            LvnChrome.Pill(_thumb, Width);
            LvnMotion.Smooth(_thumb, LvnMotion.Quick, "top", "height");
            Add(_thumb);

            if (view == null) return;
            view.verticalScroller.valueChanged += _ => Track();
            view.RegisterCallback<GeometryChangedEvent>(_ => Track());
            RegisterCallback<GeometryChangedEvent>(_ => Track());
        }

        /// <summary>Полоса вдоль правого края хозяина, во всю его высоту:
        /// хозяин — контейнер списка (position relative), список внутри.</summary>
        public static LvnSlimScroll Beside(VisualElement host, ScrollView view, float right = 4f)
        {
            var bar = new LvnSlimScroll(view);
            bar.style.top = 0; bar.style.bottom = 0; bar.style.right = right;
            host.Add(bar);
            return bar;
        }

        /// <summary>Пересчитать бегунок; всё влезает или список спрятан —
        /// полосы нет.</summary>
        public void Track()
        {
            if (_view == null) return;
            float view = _view.contentViewport.layout.height;
            float content = _view.contentContainer.layout.height;
            bool visible = _view.style.display != DisplayStyle.None
                           && _view.resolvedStyle.display != DisplayStyle.None;
            if (!visible || float.IsNaN(view) || float.IsNaN(content) || content <= view + 1f)
            {
                ScreenFx.PutAway(this);   // уход с экрана — одной дверью
                return;
            }
            style.display = DisplayStyle.Flex;
            float barH = layout.height;
            if (float.IsNaN(barH) || barH <= 1f) return;
            float thumbH = Mathf.Clamp(barH * (view / content), ThumbMin, barH);
            float p = Mathf.Clamp01(_view.scrollOffset.y / Mathf.Max(1f, content - view));
            _thumb.style.height = thumbH;
            _thumb.style.top = (barH - thumbH) * p;
        }
    }
}
