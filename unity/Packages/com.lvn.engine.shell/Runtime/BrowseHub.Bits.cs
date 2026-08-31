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
        private Button IconButton(LvnIcon icon, float size, Color color, System.Action onTap)
        {
            var b = new Button(onTap) { text = "" };
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            b.style.paddingLeft = 0; b.style.paddingRight = 0;
            b.style.paddingTop = 0; b.style.paddingBottom = 0;
            b.Add(LvnIcons.Make(icon, size, color, 0f, _theme.IconGlow));
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
            var chip = new VisualElement();
            chip.style.flexDirection = FlexDirection.Row;
            chip.style.alignItems = Align.Center;
            chip.style.backgroundColor = LvnTokens.Veil(0.28f);
            chip.style.paddingLeft = 10; chip.style.paddingRight = 10;
            chip.style.paddingTop = 4; chip.style.paddingBottom = 4;
            LvnChrome.Round(chip, 10f);
            return chip;
        }

        private VisualElement Chip(string text, Color color, LvnIcon icon = LvnIcon.None)
        {
            var chip = ChipShell();
            if (icon != LvnIcon.None)
            {
                var ic = LvnIcons.Make(icon, 18f, color, 0f, _theme.IconGlow);
                if (!string.IsNullOrEmpty(text)) ic.style.marginRight = 5;
                chip.Add(ic);
            }
            if (!string.IsNullOrEmpty(text))
            {
                var lb = new Label(text) { pickingMode = PickingMode.Ignore };
                lb.style.color = color; lb.style.fontSize = Lvn.UI.LvnFonts.Size(30f);
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
            col.style.paddingTop = 28; col.style.paddingBottom = 24;
            col.style.paddingLeft = 30; col.style.paddingRight = 30;
            return col;
        }

        private VisualElement BackBar(out Label title, System.Action onBack)
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.marginBottom = 14;
            var back = new Button(onBack) { text = _cfg.back_text ?? "‹" };
            back.style.fontSize = Lvn.UI.LvnFonts.Size(48f); back.style.minWidth = 52;
            back.style.color = _titleColor;
            back.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            LvnChrome.ClearBorder(back); LvnChrome.Round(back, _radius);
            bar.Add(back);
            title = Heading("", 30);
            title.style.marginLeft = 12;
            bar.Add(title);
            return bar;
        }
    }
}
