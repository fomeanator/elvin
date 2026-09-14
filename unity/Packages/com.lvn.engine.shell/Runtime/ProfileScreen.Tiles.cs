using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    public sealed partial class ProfileScreen
    {
        // Одна сетка у статов и значков: равные ячейки и зазоры по токенам.
        // Неполный последний ряд не растягивает единственную плитку.
        private VisualElement TileGrid(string name)
        {
            var grid = LvnFlow.Wrap(new VisualElement { name = name }, Justify.FlexStart);
            grid.style.marginBottom = LvnTokens.Space2;
            float lastWidth = -1f;
            grid.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float width = grid.contentRect.width;
                if (width <= 0f || Mathf.Approximately(width, lastWidth)) return;
                lastWidth = width;
                float gap = LvnTokens.Space2;
                int columns = Mathf.Clamp(Mathf.FloorToInt((width + gap) / (LvnTokens.TouchLg * 4f + gap)), 1, 4);
                // Дробная ширина четырёх ячеек после округления Yoga могла
                // вытолкнуть четвёртую на новую строку, где у неё нет зазора.
                float cell = Mathf.Max(0f, Mathf.Floor((width - gap * (columns - 1)) / columns));
                int i = 0;
                foreach (var tile in grid.Children())
                {
                    tile.style.width = cell;
                    tile.style.marginRight = ++i % columns == 0 ? 0f : gap;
                    tile.style.marginBottom = gap;
                }
            });
            return grid;
        }

        private VisualElement BuildStatRow()
        {
            var grid = TileGrid("profile-stats");
            foreach (var stat in Stats) grid.Add(StatTile(stat));
            return grid;
        }

        private VisualElement StatTile(Stat stat)
        {
            var tile = new VisualElement { name = "profile-stat" };
            tile.style.flexShrink = 0;
            tile.style.minWidth = 0;
            LvnChrome.Card(tile);
            StageCard(tile);
            LvnAir.Pad(tile, LvnTokens.Space2, LvnTokens.Space3);

            var value = new Label(stat.Value) { name = "profile-stat-value" };
            value.style.color = LvnTokens.Gold;
            value.style.fontSize = LvnTokens.TextLg;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            value.style.whiteSpace = WhiteSpace.Normal;
            value.style.unityTextAlign = TextAnchor.MiddleCenter;
            tile.Add(value);

            var caption = new Label(stat.Caption) { name = "profile-stat-caption" };
            ScreenUi.Quiet(caption, LvnTokens.TextXs);
            caption.style.marginTop = LvnTokens.Space1;
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            tile.Add(caption);
            return tile;
        }

        private VisualElement BuildAchievements()
        {
            var grid = TileGrid("profile-achievements");
            if (Achievements.Count > 0)
                foreach (var achievement in Achievements) grid.Add(Badge(achievement));
            else
                // Четыре безымянных замка обозначают пустую коллекцию. Это
                // не награды: ничего не добавляется в модель и нет счётчика.
                for (int i = 0; i < 4; i++)
                    grid.Add(Badge(new Achievement(LvnIcon.Lock,
                        LvnWords.Of("profile.achievement_locked", "Locked"), false)));
            return grid;
        }

        private VisualElement Badge(Achievement achievement)
        {
            var badge = new VisualElement { name = "profile-achievement" };
            badge.AddToClassList(achievement.Unlocked ? "profile-achievement-unlocked" : "profile-achievement-locked");
            badge.style.flexShrink = 0;
            badge.style.minWidth = 0;
            LvnStyler.Chip(badge, achievement.Unlocked ? LvnTokens.SurfaceHi : LvnTokens.Surface,
                padX: LvnTokens.Space2, padY: LvnTokens.Space2);
            StageCard(badge);
            var icon = LvnIcons.Make(achievement.Unlocked ? achievement.Icon : LvnIcon.Lock, LvnTokens.TextLg,
                achievement.Unlocked ? LvnTokens.Accent : LvnTokens.TextDim,
                0f, achievement.Unlocked ? LvnTheme.Current.IconGlow : 0f);
            icon.style.alignSelf = Align.Center;
            badge.Add(icon);
            var label = new Label(achievement.Title);
            ScreenUi.Quiet(label, LvnTokens.TextXs, achievement.Unlocked ? LvnTokens.Text : LvnTokens.TextDim);
            label.style.marginTop = LvnTokens.Space1;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.Add(label);
            return badge;
        }
    }
}
