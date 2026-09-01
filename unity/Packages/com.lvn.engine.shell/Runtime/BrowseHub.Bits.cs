using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// МЕЛКИЕ ЧАСТИ ЭКРАНА: кнопка-иконка, чип, колонка, шапка «назад».
    ///
    /// <para>Их зовут отовсюду, и каждая — три строки стилей. Сложенные вместе,
    /// они перестают тонуть между экранными сценариями и начинают читаться как
    /// маленький набор деталей, из которых собран весь хаб.</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        /// <summary>
        /// Круглая/квадратная кнопка с векторной иконкой вместо надписи.
        ///
        /// <para>Текст у кнопки пустой намеренно: иконка — отдельный ребёнок, и
        /// потому её цвет, толщина линии и свечение живут своей жизнью, а не
        /// наследуются от стиля текста, у которого для этого нет свойств.</para>
        /// </summary>
        private Button IconButton(LvnIcon icon, float size, Color color, System.Action onTap)
        {
            var b = new Button(onTap) { text = "" };
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            LvnAir.Pad(b, 0);
            b.Add(LvnIcons.Make(icon, size, color));
            return b;
        }

        /// <summary>
        /// ЗНАЧОК В ШАПКЕ — кнопка на тихой плашке: подарок, шестерёнка.
        ///
        /// <para>Оба писались одинаковыми четырьмя строками подряд, и размер в
        /// них стоял числом 44 при токене цели касания 48 — вторая цель
        /// касания, которую никто не объявлял. Теперь размер берётся у темы:
        /// у неё на это есть слово, и оно же держит доступность.</para>
        ///
        /// <para>Аватар рядом НЕ отсюда и не должен быть: он крупнее, носит
        /// акцентную рамку и открывает профиль — это другая роль, а не тот же
        /// значок другого размера.</para>
        /// </summary>
        private Button TopIconButton(LvnIcon icon, Color color, System.Action onTap)
        {
            var b = IconButton(icon, 24f, color, onTap);
            b.style.width = LvnTokens.Touch;
            b.style.height = LvnTokens.Touch;
            b.style.marginLeft = LvnTokens.Space2;
            b.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.Frame(b, LvnTokens.RadiusSm);
            return b;
        }

        /// <summary>
        /// ЦЕНА НА КАРТОЧКЕ — рядом ЦЕННИКА, а не своим.
        ///
        /// <para>Значок к сумме приставлял каждый экран сам, и одна валюта в
        /// разных местах выглядела по-разному — ради этого ряд и переехал в
        /// дом. Хаб остался последним, кто собирал его руками: дом звали
        /// только за цветом и значком, а складывали их обратно здесь.</para>
        ///
        /// <para>Огранка плашки остаётся тут: ярлык лежит НА ОБЛОЖКЕ, и
        /// читаемость ему даёт вуаль, а не тон панели.</para>
        /// </summary>
        private VisualElement CostChip(LvnCost cost)
        {
            var chip = ChipShell();
            chip.Add(Lvn.UI.LvnPriceTag.Tag(cost?.currency, cost?.amount ?? 0,
                new Lvn.UI.LvnPriceTag.Row { FontSize = 30f, IconSize = 18f, Gap = 5f }));
            return chip;
        }

        /// <summary>Плашка ярлыка: вуаль, отступы, скругление. Содержимое
        /// кладёт вызывающий — цену собирает Ценник, слово со значком
        /// собирается ниже.</summary>
        private VisualElement ChipShell()
        {
            return LvnStyler.Chip(ScreenUi.Row(), LvnTokens.Veil(0.28f));
        }

        /// <summary>Метка на карточке: иконка, подпись или и то и другое.
        /// Пустой текст — иконка одна, и метка сжимается до неё.</summary>
        private VisualElement Chip(string text, Color color, LvnIcon icon = LvnIcon.None)
        {
            var chip = ChipShell();
            if (icon != LvnIcon.None)
            {
                var ic = LvnIcons.Make(icon, 18f, color);
                if (!string.IsNullOrEmpty(text)) ic.style.marginRight = LvnTokens.Tight;
                chip.Add(ic);
            }
            if (!string.IsNullOrEmpty(text))
            {
                var lb = new Label(text) { pickingMode = PickingMode.Ignore };
                lb.style.color = color; lb.style.fontSize = LvnTokens.TextBase;
                chip.Add(lb);
            }
            return chip;
        }

        // ── shared layout bits ──
        private VisualElement Column()
        {
            var col = new VisualElement();
            ScreenUi.Stretch(col);
            col.style.flexDirection = FlexDirection.Column;
            LvnAir.Pad(col, LvnEdges.PageSide, LvnTokens.Space4);
            return col;
        }

        private VisualElement BackBar(out Label title, System.Action onBack)
        {
            var bar = ScreenUi.Row();
            bar.style.marginBottom = LvnTokens.Space2;
            // Источник, а не готовая строка: при смене языка подпись обязана
            // спроситься заново (правило Переодевания).
            var back = Lvn.UI.LvnRedress.Bind(new Button(onBack),
                () => LvnWords.Pick("hub.back", _cfg.back_text, "‹"));
            back.style.fontSize = LvnTokens.TextXl; back.style.minWidth = LvnTokens.Touch;
            LvnStyler.Plate(back, LvnTokens.Faint, _titleColor, _radius);
            bar.Add(back);
            title = Heading("", 30);
            title.style.marginLeft = LvnTokens.Space2;
            bar.Add(title);
            return bar;
        }
    }
}
