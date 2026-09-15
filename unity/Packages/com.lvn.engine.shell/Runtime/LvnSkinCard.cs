using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ПЛИТКА СКИНА — один облик на гардероб и «Что внутри» круток (Илья 15.09:
    /// «нужен такой же попап со скинами из гардероба», «наш стиль самое
    /// главное»). Платиновый задник, арт с кадрированием по разделу,
    /// вешалка-плейсхолдер, тёмная подложка имени, ценник или подарок и облик
    /// редкости «как в Доте». Здесь только вид: поведение (тап, загрузка арта)
    /// остаётся у хозяина плитки — у него и данные, и монтажёр.
    /// </summary>
    public static class LvnSkinCard
    {
        public const float Width = 150f, Height = 208f;
        // Платина #D1D1D6 (Илья 26.08) вместо прежней тускло-серой заливки:
        // арт скинов тёмный, и светлый задник держит его силуэт.
        public static readonly Color Platinum = UiColor.Named("#D1D1D6", new Color(0.82f, 0.82f, 0.84f));
        public static readonly Color PlateDark = new Color(0.16f, 0.16f, 0.19f, 0.85f);

        /// <summary>Части плитки, которые хозяин обновляет: арт, вешалка, имя.</summary>
        public sealed class Parts
        {
            public VisualElement Card, Art, Placeholder, Plate, Bar;
            public Label Name;
        }

        /// <summary>Родить плитку. <paramref name="zoom"/> и <paramref name="anchorY"/> —
        /// кадрирование арта по разделу (причёска — к голове, платье — к
        /// корпусу); <paramref name="none"/> — пункт «снять», у него глиф «×».</summary>
        public static Parts Make(float radius, float zoom, float anchorY, bool none, Func<string> caption, Color text)
        {
            var card = new VisualElement();
            card.style.width = Width; card.style.height = Height;
            card.style.flexShrink = 0;
            card.style.backgroundColor = Platinum;
            LvnChrome.Round(card, radius);
            card.style.overflow = Overflow.Hidden; // арт и подложка не выходят за скругление

            var art = new VisualElement { name = "card-art", pickingMode = PickingMode.Ignore };
            art.style.position = Position.Absolute;
            art.style.width = Length.Percent(zoom * 100f);
            art.style.height = Length.Percent(zoom * 100f);
            art.style.left = Length.Percent(50f - zoom * 100f * 0.50f); // якорь X в центре окна
            art.style.top = Length.Percent(50f - zoom * 100f * anchorY); // якорь Y в центре окна
            LvnPicture.Fit(art, cover: false);
            card.Add(art);

            // Вешалка, пока арт едет: пустая плитка читалась как «не грузит».
            // Тёмный глиф — задник светлый, светлый значок на нём растворялся бы.
            var ph = LvnIcons.Make(none ? LvnIcon.Close : LvnIcon.Wardrobe, 42f, new Color(0.18f, 0.18f, 0.22f));
            ph.pickingMode = PickingMode.Ignore;
            ph.name = "card-ph";
            ph.style.position = Position.Absolute;
            ph.style.left = Length.Percent(50f);
            ph.style.top = Length.Percent(38f);
            ph.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
            ph.style.opacity = 0.55f;
            card.Add(ph);

            var plate = new VisualElement { name = "card-plate", pickingMode = PickingMode.Ignore };
            LvnChrome.BottomStrip(plate);
            plate.style.backgroundColor = PlateDark;
            LvnAir.Pad(plate, LvnTokens.Space1);
            card.Add(plate);

            // Полоса редкости понизу — как у карточек Доты; зажигает DressRarity.
            var bar = new VisualElement { name = "card-rarity", pickingMode = PickingMode.Ignore };
            LvnChrome.BottomStrip(bar);
            bar.style.height = 4f;
            bar.style.display = DisplayStyle.None;
            card.Add(bar);

            var name = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore }, caption);
            name.name = "card-name";
            name.style.color = text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            plate.Add(name);

            return new Parts { Card = card, Art = art, Placeholder = ph, Plate = plate, Bar = bar, Name = name };
        }

        /// <summary>ОБЛИК РЕДКОСТИ «КАК В ДОТЕ» (TR-109, Илья 15.09: «хочу скины
        /// в цвет Доты»). Не один ободок: задник плитки уходит в цвет ступени,
        /// подложка имени темнеет в него же, имя пишется этим цветом, понизу —
        /// яркая полоса. У обычного всё почти платиновое, у бессмертного —
        /// золото: ступень видна с расстояния, а не по тонкой рамке.</summary>
        public static void DressRarity(VisualElement card, Color? rarity, Color text)
        {
            var plate = card.Q("card-plate");
            var name = card.Q<Label>("card-name");
            var bar = card.Q("card-rarity");
            if (!rarity.HasValue)
            {
                LvnChrome.ClearBorder(card);
                card.style.backgroundColor = Platinum;
                if (plate != null) { plate.style.backgroundColor = PlateDark; plate.style.paddingBottom = LvnTokens.Space1; }
                if (name != null) name.style.color = text;
                if (bar != null) bar.style.display = DisplayStyle.None;
                return;
            }
            var c = rarity.Value;
            LvnChrome.Border(card, c, 2f);
            card.style.backgroundColor = Color.Lerp(Platinum, c, 0.45f);
            if (plate != null)
            {
                var tinted = Color.Lerp(PlateDark, c, 0.35f); tinted.a = 0.9f;
                plate.style.backgroundColor = tinted;
                plate.style.paddingBottom = LvnTokens.Space1 + 4f;   // место под полосу
            }
            if (name != null) name.style.color = Color.Lerp(c, Color.white, 0.3f);
            if (bar != null) { bar.style.backgroundColor = c; bar.style.display = DisplayStyle.Flex; }
        }

        /// <summary>Подарок вместо ценника: приз круток (TR-93) не покупают,
        /// а выигрывают — ценник «0» врал бы «бесплатно».</summary>
        public static VisualElement GiftBadge()
        {
            var gift = Chip("card-price", top: 6, right: 6);
            gift.Add(LvnIcons.Make(LvnIcon.Gift, 20f, LvnTokens.Gold));
            return gift;
        }

        /// <summary>Ценник значком, а не словом: со словом ярлык шире плитки
        /// (Илья 28.08). Цвет — у ценника по валюте предмета.</summary>
        public static VisualElement PriceBadge(string currency, long price)
        {
            var badge = LvnPriceTag.Tag(currency, price, new LvnPriceTag.Row { FontSize = 19f, Gap = 3f });
            badge.name = "card-price";
            badge.style.position = Position.Absolute;
            badge.style.top = 6; badge.style.right = 6;
            badge.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnAir.Pad(badge, LvnTokens.Space1, LvnTokens.Hair);
            LvnChrome.Round(badge, LvnTokens.RadiusSm);
            return badge;
        }

        /// <summary>Пометка в левом верхнем углу — шанс, «есть»: короткое слово
        /// цветом ступени на тёмной подложке.</summary>
        public static Label Mark(string text, Color color)
        {
            var mark = new Label(text) { name = "card-mark", pickingMode = PickingMode.Ignore };
            mark.style.position = Position.Absolute;
            mark.style.top = 6; mark.style.left = 6;
            mark.style.color = color;
            mark.style.fontSize = LvnTokens.TextSm;
            mark.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnAir.Pad(mark, LvnTokens.Space1, LvnTokens.Hair);
            LvnChrome.Round(mark, LvnTokens.RadiusSm);
            return mark;
        }

        private static VisualElement Chip(string name, float top, float right)
        {
            var chip = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            chip.style.position = Position.Absolute;
            chip.style.top = top; chip.style.right = right;
            chip.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnAir.Pad(chip, LvnTokens.Space1, LvnTokens.Hair);
            LvnChrome.Round(chip, LvnTokens.RadiusSm);
            return chip;
        }
    }
}
