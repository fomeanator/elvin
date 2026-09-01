using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ПОТОК — строка, которая переносится.
    ///
    /// <para>Плитки галереи, чипсы жанров, вкладки лавки, семь дней награды,
    /// кнопки настроек: всё это «поставить в ряд, а не поместилось — перенести
    /// на следующую строку». Четырнадцать мест, и в каждом одна и та же тройка
    /// — создать элемент, повернуть в строку, разрешить перенос.</para>
    ///
    /// <para>Различает места только ВЫРАВНИВАНИЕ: плитки галереи жмутся влево,
    /// наборы лавки расходятся по краям, семь дней стоят по центру. Оно тонет
    /// среди двух строк, одинаковых везде, — а перенос, забытый в одном из
    /// мест, даёт не ошибку, а ряд, уезжающий за край экрана.</para>
    ///
    /// <para>Живёт в движке, а не в оболочке: сетки есть по обе стороны —
    /// галерея и выборы принадлежат сцене, лавка и профиль оболочке.</para>
    /// </summary>
    public static class LvnFlow
    {
        /// <summary>Новая строка с переносом.</summary>
        public static VisualElement Wrap(Justify? justify = null)
            => Wrap(new VisualElement(), justify);

        /// <summary>Сделать потоком уже созданный элемент — в том числе чужой
        /// (<c>contentContainer</c> прокрутки, готовый ряд экрана).
        ///
        /// <para>Выравнивание НЕОБЯЗАТЕЛЬНО и ставится, только если названо.
        /// Иначе дом был бы негоден там, где ряд пришёл готовым: у строки
        /// настроек выравнивание задал <c>ScreenUi.Row(spread: true)</c>, и
        /// молча сбросить его значило бы чинить перенос ценой вёрстки.</para>
        /// </summary>
        public static T Wrap<T>(T el, Justify? justify = null) where T : VisualElement
        {
            if (el == null) return null;
            el.style.flexDirection = FlexDirection.Row;
            el.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap;
            if (justify.HasValue) el.style.justifyContent = justify.Value;
            return el;
        }
    }
}
