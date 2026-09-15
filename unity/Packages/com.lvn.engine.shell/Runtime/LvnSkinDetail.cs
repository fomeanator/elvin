using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>ПОДРОБНОСТИ СКИНА — крупно, по долгому нажатию на плитку (или по
    /// короткому, когда у плитки нет своего действия): чёрная вуаль, арт во всю
    /// ширину с крутилкой, имя цветом ступени, строка «ступень · цена», способ
    /// получения (Илья 15.09: «надо у скинов писать их способы получения»);
    /// тап где угодно закрывает.</summary>
    public static class LvnSkinDetail
    {
        private const float DetailZoom = 1.35f;

        public static void Show(VisualElement from, LvnSkinCard.Info info, ILvnAssets assets)
        {
            var root = from?.panel?.visualTree;
            if (root == null || info == null) return;
            var veil = new VisualElement { name = "skin-detail" };
            LvnChrome.Stretch(veil);
            veil.style.backgroundColor = UiColor.WithAlpha(Color.black, 0.96f);
            veil.style.alignItems = Align.Center; veil.style.justifyContent = Justify.Center;
            veil.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            veil.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); veil.RemoveFromHierarchy(); });

            var column = new VisualElement();
            column.style.width = Length.Percent(88f);
            column.style.alignItems = Align.Center;
            var color = info.Rarity ?? LvnTokens.Gold;

            // Арт — той же плиткой, только большой: имя — на картинке, в её
            // подложке, как у маленькой (Илья 15.09 со скрином); у плитки свой
            // лоадер с крутилкой и тот же кадр по разделу.
            var art = new LvnSkinCard { pickingMode = PickingMode.Ignore };
            float h = LvnStageKit.D(320f);
            art.SetSize(h * LvnSkinCard.BaseWidth / LvnSkinCard.BaseHeight, h);
            art.Bind(new LvnSkinCard.Info
            {
                // Ближе, чем на плитке (Илья: «показывать скин приближенным, как
                // в гардеробе»): тот же якорь, кадр крупнее.
                Title = info.Title, Art = info.Art, SharpArt = true, Frame = info.Frame * DetailZoom, FrameY = info.FrameY, Cover = info.Cover,
                None = info.None, Rarity = info.Rarity, Owned = true, Radius = info.Radius, TextColor = info.TextColor,
            }, assets);
            column.Add(art);

            var line = new List<string>();
            if (!string.IsNullOrEmpty(info.RarityWord)) line.Add(info.RarityWord);
            if (info.Price > 0) line.Add(LvnPriceTag.Full(info.Currency, info.Price));
            if (line.Count > 0)
            {
                var meta = Meta(string.Join(" · ", line), color, LvnTokens.TextBase);
                meta.style.marginTop = LvnTokens.Space2;
                column.Add(meta);
            }
            if (!string.IsNullOrEmpty(info.Obtain)) column.Add(Meta(info.Obtain, LvnTokens.TextDim, LvnTokens.TextSm));

            veil.Add(column);
            root.Add(veil);
            veil.BringToFront();
        }

        private static Label Meta(string text, Color color, float size)
        {
            var meta = new Label(text) { pickingMode = PickingMode.Ignore };
            meta.style.fontSize = size; meta.style.color = color;
            meta.style.whiteSpace = WhiteSpace.Normal; meta.style.unityTextAlign = TextAnchor.MiddleCenter;
            meta.style.alignSelf = Align.Stretch; meta.style.marginTop = LvnTokens.Hair;
            return meta;
        }
    }
}
