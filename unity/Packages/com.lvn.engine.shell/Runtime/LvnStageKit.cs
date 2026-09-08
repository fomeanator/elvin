using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НАБОР ДЕТАЛЕЙ ОБЛИКА «СЦЕНА» — кнопка, плашка-заголовок, полоса
    /// прогресса, картинка рамки и подпись, из которых собирается главная по
    /// макету партнёра (см. <see cref="BrowseHub"/>, Stage).
    ///
    /// <para>Детали здесь, а не в хабе, потому что тот же рисунок ждут магазин
    /// и профиль: одна кнопка на три экрана, а не три похожих. Правило деления
    /// простое — РАМКА нарисована (свечение, срезы, блик код не рисует), всё
    /// ЖИВОЕ собрано элементами: слово из словаря и шрифта темы, число из
    /// данных, ход полосы из прогресса, нажатие с откликом.</para>
    ///
    /// <para>Размеры — в единицах МАКЕТА (390 dp шириной) через один множитель
    /// <see cref="D"/>: числа макета читаются как есть.</para>
    /// </summary>
    internal static class LvnStageKit
    {
        /// <summary>Ширина холста макета, dp.</summary>
        public const float DesignWidth = 390f;
        /// <summary>Холст макета против панели 1080: один множитель на все размеры.</summary>
        public const float K = 1080f / DesignWidth;
        public static float D(float dp) => Mathf.Round(dp * K);

        /// <summary>Запас, с которым экспортированы рамки: свечение выходит за
        /// край элемента на 12 dp с каждой стороны.</summary>
        public const float Bleed = 12f;

        /// <summary>Имя картинки рамки — по нему фотограф ждёт, пока весь арт
        /// облика доедет.</summary>
        public const string ArtName = "stage-img";

        /// <summary>РАМКИ ОБЛИКА — ровно те файлы, что кладёт главная
        /// (BrowseHub.Stage) и шапка («плюс»). Список один: по нему бут греет
        /// витрину до снятия вуали, по нему же автор знает, что рисовать.</summary>
        public static readonly string[] SkinFiles =
            { "nav.png", "panel.png", "card-back.png", "card-front.png", "adv.png", "plus.png" };

        /// <summary>Адрес файла в папке облика — папка с косой чертой на
        /// конце или без неё.</summary>
        public static string SkinUrl(string root, string file)
        {
            root ??= "";
            return (root.EndsWith("/") ? root : root + "/") + file;
        }

        /// <summary>Поставить элемент абсолютно: место и размер одним вызовом.</summary>
        public static T At<T>(T el, float x, float y, float w, float h) where T : VisualElement
        {
            el.style.position = Position.Absolute;
            el.style.left = x; el.style.top = y;
            el.style.width = w; el.style.height = h;
            return el;
        }

        /// <summary>Картинка рамки на своём месте: больше места на запас
        /// свечения с каждой стороны — так нарисована.</summary>
        public static VisualElement Art(string url, ILvnAssets assets,
                                        float x, float y, float w, float h, float bleed = Bleed)
        {
            var img = new VisualElement { name = ArtName, pickingMode = PickingMode.Ignore };
            At(img, x - D(bleed), y - D(bleed), w + D(bleed * 2f), h + D(bleed * 2f));
            LvnPicture.Skin(img, url, assets, what: "StageSkin");
            return img;
        }

        /// <summary>Подпись облика по центру своего места. Источник слова
        /// привязывается к переодеванию (смена языка); без источника — подпись,
        /// чей текст ведёт сам экран (число награды). Среднее начертание —
        /// заголовочное темы, и берётся у неё, а не файлом по имени.</summary>
        public static Label Text(Func<string> text, float size, Color color, bool medium = false)
        {
            var l = text != null ? LvnRedress.Bind(new Label(), text) : new Label();
            l.pickingMode = PickingMode.Ignore;
            l.style.fontSize = size;
            l.style.color = color;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            if (medium) LvnFonts.Apply(l, LvnFonts.Display);
            return l;
        }

        /// <summary>Плашка-заголовок: слово прописными на нарисованной плашке.
        /// Место задаёт вызывающий — плашка нарисована в рамке.</summary>
        public static Label Plaque(Func<string> text)
            => Text(() => (text() ?? string.Empty).ToUpperInvariant(), LvnTokens.TextSm, LvnTokens.Text);

        /// <summary>Кнопка облика: рамка нарисована в панели, здесь слово и зона
        /// нажатия с откликом. Подпись стоит чуть выше центра — у нарисованной
        /// кнопки нижняя грань толще.</summary>
        public static VisualElement Button(Func<string> text, Action onTap)
        {
            var b = new VisualElement();
            b.style.justifyContent = Justify.Center;
            b.style.alignItems = Align.Center;
            b.style.paddingBottom = D(3f);
            b.Add(Text(() => (text() ?? string.Empty).ToUpperInvariant(), LvnTokens.TextBase, LvnTokens.Gold, medium: true));
            b.AddManipulator(new Clickable(onTap));
            LvnMotion.Tappable(b);
            return b;
        }

        /// <summary>Полоса прогресса макета: чёрная дорожка, тёмная канавка,
        /// светлый ход слева. Ход двигает <see cref="Fill"/>.</summary>
        public static VisualElement Progress(out VisualElement fill)
        {
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.height = D(9f);
            bar.style.backgroundColor = Color.black;
            LvnChrome.Round(bar, D(3f));
            var groove = new VisualElement { pickingMode = PickingMode.Ignore };
            groove.style.position = Position.Absolute;
            groove.style.left = D(2f); groove.style.right = D(2f); groove.style.top = D(2f); groove.style.bottom = D(2f);
            groove.style.backgroundColor = LvnTokens.Track;
            LvnChrome.Round(groove, D(3f));
            bar.Add(groove);
            fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.style.position = Position.Absolute;
            fill.style.left = D(2f); fill.style.top = D(2f); fill.style.bottom = D(2f);
            fill.style.width = Length.Percent(0f);
            fill.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Round(fill, D(3f));
            bar.Add(fill);
            return bar;
        }

        /// <summary>Доля хода 0..1 — от ширины канавки.</summary>
        public static void Fill(VisualElement fill, float fraction)
        {
            if (fill != null) fill.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100f);
        }
    }
}
