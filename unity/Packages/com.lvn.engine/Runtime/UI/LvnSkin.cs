using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Текстуры интерфейса вместо сплошных заливок.
    ///
    /// <para>Панель, закрашенная одним цветом из кода, читается как пластик — в
    /// любой системе, не только в UI Toolkit. Настоящая «дороговизна» приходит
    /// не от движка, а от материала: зерно, неровная кромка, свет, падающий с
    /// одной стороны. Здесь эти материалы подставляются одной строкой вместо
    /// <c>style.backgroundColor</c>.</para>
    ///
    /// <para>РАСТЯЖЕНИЕ ПО ДЕВЯТИ ЗОНАМ. Углы остаются как есть, стороны тянутся
    /// в одну сторону, центр — в обе. Ширина угловой зоны у каждой текстуры
    /// СВОЯ и посчитана по её фактическому радиусу: номинал из задания не
    /// подошёл — при нём у кнопки-контура от рамки оставалась одна линия.</para>
    ///
    /// <para>Скин необязателен. Нет текстуры — элемент остаётся с прежней
    /// заливкой, и интерфейс работает: движок без арта обязан оставаться
    /// движком.</para>
    /// </summary>
    public static class LvnSkin
    {
        /// <summary>Имена текстур. Строкой, а не enum: скин подменяем целиком
        /// (тема новеллы), и лишний слой перечислений тут только мешает.</summary>
        public const string PanelSurface = "panel_surface";
        public const string PanelRaised = "panel_raised";
        public const string PanelSunken = "panel_sunken";
        public const string ButtonPrimary = "button_primary";
        public const string ButtonSecondary = "button_secondary";
        public const string Chip = "chip";
        public const string CardFrame = "card_frame";
        public const string Divider = "divider";
        public const string SheetTop = "sheet_top";

        // Угловые зоны В ПИКСЕЛЯХ ТЕКСТУРЫ. Числа не из задания, а измерены по
        // готовым файлам: у каждой картинки радиус свой, и зона, взятая «на
        // глаз», режет скругление пополам — кромка при растяжении рвётся.
        private static readonly Dictionary<string, int> Slice = new Dictionary<string, int>
        {
            { PanelSurface, 16 }, { PanelRaised, 32 }, { PanelSunken, 24 },
            { ButtonPrimary, 26 }, { ButtonSecondary, 63 }, { Chip, 35 },
            { CardFrame, 95 }, { Divider, 96 }, { SheetTop, 30 },
        };

        // Текстуры лежат в двойном разрешении: на телефоне это чёткость, а
        // масштаб 0,5 возвращает зонам их экранный размер. Без него кромка
        // рисуется вдвое толще, чем задумано.
        private const float TextureScale = 0.5f;

        private const string Folder = "ui/";
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        /// <summary>Достаёт текстуру, кэшируя. null — если её нет в сборке.</summary>
        public static Texture2D Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_cache.TryGetValue(name, out var t)) return t;
            t = Resources.Load<Texture2D>(Folder + name);
            _cache[name] = t;
            return t;
        }

        /// <summary>
        /// Кладёт текстуру фоном с правильной нарезкой. Возвращает false, если
        /// текстуры нет, — вызывающий тогда оставляет свою заливку.
        /// </summary>
        public static bool Apply(VisualElement el, string name)
        {
            if (el == null) return false;
            var tex = Get(name);
            if (tex == null) return false;

            el.style.backgroundImage = new StyleBackground(tex);
            int s = Slice.TryGetValue(name, out var v) ? v : 16;
            el.style.unitySliceLeft = s;
            el.style.unitySliceRight = s;
            el.style.unitySliceTop = s;
            el.style.unitySliceBottom = s;
            el.style.unitySliceScale = TextureScale;

            // Заливка и рамка снимаются: под текстурой они дают ободок чужого
            // цвета по краю скругления — тот самый «пластиковый» контур, из-за
            // которого всё и затевалось.
            el.style.backgroundColor = Color.clear;
            el.style.borderLeftWidth = 0;
            el.style.borderRightWidth = 0;
            el.style.borderTopWidth = 0;
            el.style.borderBottomWidth = 0;
            return true;
        }

        /// <summary>
        /// Скругление больше не нужно: оно нарисовано в самой текстуре.
        /// Оставленный радиус обрезает кромку и съедает её неровность —
        /// ровно ту, ради которой текстура и рисовалась.
        /// </summary>
        public static void ApplyPanel(VisualElement el, string name = PanelSurface)
        {
            if (!Apply(el, name)) return;
            el.style.borderTopLeftRadius = 0;
            el.style.borderTopRightRadius = 0;
            el.style.borderBottomLeftRadius = 0;
            el.style.borderBottomRightRadius = 0;
        }
    }
}
