using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>Shared UI Toolkit styling helpers for the reference components, so
    /// the dialogue box and choice list skin their panels the same way.</summary>
    public static class UiStyle
    {
        /// <summary>ОКНО в дом картинки: поставить готовый спрайт фоном.
        ///
        /// <para>Роль жила здесь отдельным домом в одну работу и разошлась с
        /// показом по адресу в правиле про углы. Работа переехала к картинке,
        /// имя осталось: его знают четыре опорных компонента.</para></summary>
        public static void ApplyBackground(VisualElement el, Sprite sprite, int slice)
            => LvnPicture.Paint(el, sprite, slice);
    }
}
