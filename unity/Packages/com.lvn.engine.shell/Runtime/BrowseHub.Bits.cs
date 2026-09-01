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

        /// <summary>Метка на карточке: иконка, подпись или и то и другое.
        /// Пустой текст — иконка одна, и метка сжимается до неё.</summary>
        /// <summary>
        /// ЧИП ЦЕНЫ — облик берёт Ценник, а не эта функция.
        ///
        /// <para>Цена рисовалась в ЧЕТЫРЁХ местах хаба одинаковой строкой, и во
        /// всех четырёх значок был зашит энергией, а цвет золотом. Валюта при
        /// этом приходит из данных: новелла, назначившая вход за кристаллы,
        /// показывала «⚡ 100» — чужой значок при своей сумме. Разряды тоже
        /// терялись: сумма шла через ToString(), мимо разделителя языка.</para>
        /// </summary>
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
            var chip = ScreenUi.Row();
            chip.style.backgroundColor = LvnTokens.Veil(0.28f);
            LvnAir.PadX(chip, LvnTokens.Space2);
            LvnAir.PadY(chip, LvnTokens.Tight);
            LvnChrome.Round(chip, LvnTokens.RadiusSm);
            return chip;
        }

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
            LvnAir.PadX(col, LvnEdges.PageSide);
            LvnAir.PadY(col, LvnTokens.Space4);
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
            back.style.fontSize = LvnTokens.TextXl; back.style.minWidth = 52;
            back.style.color = _titleColor;
            back.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(back); LvnChrome.Round(back, _radius);
            bar.Add(back);
            title = Heading("", 30);
            title.style.marginLeft = LvnTokens.Space2;
            bar.Add(title);
            return bar;
        }
    }
}
