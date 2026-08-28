using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// КАРТИНКА В СВОЁМ МЕСТЕ — как она вписывается в отведённый кадр.
    ///
    /// <para>Дом стоит в UI-слое ДВИЖКА, а не оболочки: вписывание нужно и фону
    /// темы, и слою интерфейса, а они про оболочку не знают (границы сборок).</para>
    /// </summary>
    public static class LvnPicture
    {
        /// <summary>
        /// КАК КАРТИНКА ВПИСЫВАЕТСЯ В СВОЁ МЕСТО — три строки стиля, которые
        /// стояли двадцатью пятью копиями.
        ///
        /// <para>Правило простое: заполнить кадр без полей (<c>Cover</c>) или
        /// показать целиком (<c>Contain</c>), по центру и без размножения. Но
        /// написано оно было по месту, и хватало забыть ОДНУ строку из трёх,
        /// чтобы получить свой баг: без центрирования картинка липнет к
        /// левому-верхнему углу, без запрета повтора мелкий арт замостит плитку
        /// собой, а без режима вписывания растянется по кадру.</para>
        ///
        /// <para>Здесь же место для будущего общего решения: рамка сглаживания,
        /// поведение при отсутствующем арте, ступень качества. Пока их правит
        /// каждый экран сам, менять правило нельзя — только повторять.</para>
        /// </summary>
        public static T Fit<T>(T el, bool cover = true) where T : VisualElement
        {
            if (el == null) return el;
            el.style.backgroundSize = new BackgroundSize(
                cover ? BackgroundSizeType.Cover : BackgroundSizeType.Contain);
            el.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            el.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            el.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            return el;
        }

        /// <summary>
        /// ЧТО НА ЭКРАНЕ — НЕ ТРОГАТЬ. Закрепить спрайт за элементом, пока тот
        /// в панели: кэш вытесняет по давности использования и не знает, что
        /// картинку прямо сейчас показывают.
        ///
        /// <para>Правило родилось из живого бага 27.08 — обложки в хабе белели
        /// после прогулки по гардеробу, арт героини после главы. Починили его
        /// в оболочке, там пин и остался; ядро сцены оболочку не видит, и
        /// галерея CG внутриигрового меню грузила картинки МИМО пина. Тот же
        /// баг, тот же экран, только другая дверь — а по коду не видно, потому
        /// что дом стоял этажом выше (см. роль «дом стоял не на том этаже»).</para>
        ///
        /// <para>Пин снимается сам, когда элемент уходит из панели. Повторный
        /// показ другой картинки отпускает прежнюю: держать обе значило бы
        /// запирать память ровно тем, что игрок уже пролистал.</para>
        /// </summary>
        public static void Pin(VisualElement el, Sprite sprite, ILvnAssets assets)
        {
            var loader = (assets as CachingAssets)?.Loader;
            if (el == null || loader == null || sprite == null) return;
            if (_pins.TryGetValue(el, out var old))
            {
                if (ReferenceEquals(old.sprite, sprite)) return;
                old.loader?.PinSprite(old.sprite, false);
                loader.PinSprite(sprite, true);
                _pins[el] = (loader, sprite);
                return;
            }
            loader.PinSprite(sprite, true);
            _pins[el] = (loader, sprite);
            el.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                // Guard по словарю: дубль-колбэк после повторного Attach
                // становится no-op — пин снимается ровно один раз.
                if (_pins.TryGetValue(el, out var cur))
                {
                    cur.loader?.PinSprite(cur.sprite, false);
                    _pins.Remove(el);
                }
            });
        }

        private static readonly System.Collections.Generic.Dictionary<
            VisualElement, (Lvn.Content.ContentLoader loader, Sprite sprite)> _pins
            = new System.Collections.Generic.Dictionary<
                VisualElement, (Lvn.Content.ContentLoader, Sprite)>();

        /// <summary>Подложка под картинку: элемент, который не ловит касания и
        /// уже знает, как вписывать арт. Ровно то, что писали руками перед
        /// каждой загрузкой фона (<c>ScreenUi.AssignBgAsync</c>).</summary>
        public static VisualElement Picture(bool cover = true)
            => Fit(new VisualElement { pickingMode = PickingMode.Ignore }, cover);
    }
}
