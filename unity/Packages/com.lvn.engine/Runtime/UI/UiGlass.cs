using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ПОДЛОЖКА ОКНА — ПРОСТАЯ ПРОЗРАЧНОСТЬ. Полупрозрачный слой под
    /// содержимым: мир виден сквозь панель реплики, кнопки выбора и листы
    /// оболочки, а текст поверх остаётся читаемым.
    ///
    /// <para>ЗДЕСЬ БЫЛО МАТОВОЕ СТЕКЛО, И ОНО СТОИЛО КАДРА. Камера мира снимала
    /// себя в отдельную <c>RenderTexture</c>, уменьшала втрое, гоняла два
    /// прохода размытия и раздавала копию окнам — каждые 1/15 секунды, плюс
    /// выравнивание копии под каждое окно и опрос ссылки четырежды в секунду.
    /// На слабом телефоне это съедало кадры ровно там, где их ждут больше
    /// всего: в диалоге и на выборе («лагает, хочу 60 fps на слабых; удали
    /// такое стекло, замени на простую прозрачность» — Илья 09.09).</para>
    ///
    /// <para>Договор снаружи не изменился: <see cref="Apply"/> с силой 0 снимает
    /// слой, с силой больше нуля кладёт тон под содержимое, а
    /// <c>ui.dialogue.glass</c> / <c>ui.choices.glass</c> из манифеста
    /// по-прежнему решают, насколько он плотный. Изменилось только то, ЧТО
    /// видно за тоном: сам мир, а не его размытая копия.</para>
    /// </summary>
    public static class UiGlass
    {
        private const string LayerName = "lvn-glass";

        /// <summary>Положить тон под содержимое <paramref name="host"/>.
        /// <paramref name="strength"/> 0 — снять (элемент возвращается к своей
        /// заливке), 1 — тон во всю названную плотность.</summary>
        public static void Apply(VisualElement host, float strength, Color tint)
        {
            if (host == null) return;
            var layer = host.Q(LayerName);
            if (strength <= 0.004f)
            {
                layer?.RemoveFromHierarchy();
                return;
            }
            if (layer == null)
            {
                layer = new VisualElement { name = LayerName, pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(layer);
                host.Insert(0, layer);       // под содержимым, но внутри скругления
                // Скруглённое окно обрезает подложку только при overflow:hidden —
                // иначе прямоугольник тона торчит из закруглённых углов.
                host.style.overflow = Overflow.Hidden;
            }
            var t = tint;
            t.a *= Mathf.Clamp01(strength);
            layer.style.backgroundColor = t;
        }

        /// <summary>Есть ли на элементе подложка (нужно тем, кто решает, красить
        /// ли его обычной заливкой).</summary>
        public static bool IsOn(VisualElement host) => host?.Q(LayerName) != null;
    }
}
