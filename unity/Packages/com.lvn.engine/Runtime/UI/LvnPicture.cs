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

        /// <summary>Подложка под картинку: элемент, который не ловит касания и
        /// уже знает, как вписывать арт. Ровно то, что писали руками перед
        /// каждой загрузкой фона (<c>ScreenUi.AssignBgAsync</c>).</summary>
        public static VisualElement Picture(bool cover = true)
            => Fit(new VisualElement { pickingMode = PickingMode.Ignore }, cover);
    }
}
