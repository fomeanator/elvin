using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Плашка достижения — единственное, что движок делает с достижениями сам.
    ///
    /// <para>Всё остальное уже есть: достижение хранится в межновелльной
    /// переменной (`global.ach_<id>`), значит персистится, переживает главы и
    /// уезжает на сервер вместе с прочим состоянием. Новой команды языка не
    /// появилось, а плеер лишь сообщает о новой записи.</para>
    ///
    /// <para>Показ намеренно скромный: сверху, поверх сцены, три секунды. В
    /// новелле достижение не должно перебивать реплику — оно поздравляет, а не
    /// прерывает.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        private VisualElement _achCard;
        private Label _achTitle, _achName;
        private IVisualElementScheduledItem _achHide;

        private void HookAchievements()
        {
            if (_player == null) return;
            _player.AchievementUnlocked -= OnAchievement;
            _player.AchievementUnlocked += OnAchievement;
        }

        private void OnAchievement(string id, string title)
        {
            if (_labelLayer == null || string.IsNullOrEmpty(title)) return;

            if (_achCard == null)
            {
                _achCard = new VisualElement { name = "vn-achievement", pickingMode = PickingMode.Ignore };
                _achCard.style.position = Position.Absolute;
                _achCard.style.left = Length.Percent(50);
                _achCard.style.top = Length.Percent(6);
                _achCard.style.translate = new Translate(Length.Percent(-50), 0);
                _achCard.style.maxWidth = Length.Percent(80);
                _achCard.style.paddingLeft = 22; _achCard.style.paddingRight = 22;
                _achCard.style.paddingTop = 10; _achCard.style.paddingBottom = 12;
                _achCard.style.alignItems = Align.Center;

                _achTitle = new Label { pickingMode = PickingMode.Ignore };
                _achTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
                _achName = new Label { pickingMode = PickingMode.Ignore };
                _achName.style.unityTextAlign = TextAnchor.MiddleCenter;
                _achName.style.whiteSpace = WhiteSpace.Normal;
                _achCard.Add(_achTitle);
                _achCard.Add(_achName);
                _labelLayer.Add(_achCard);
            }

            var bg = Theme != null ? Theme.PanelColor : new Color(0.05f, 0.05f, 0.08f, 0.92f);
            _achCard.style.backgroundColor = bg;
            float r = Theme != null ? Theme.PanelCornerRadius : 12f;
            _achCard.style.borderTopLeftRadius = r; _achCard.style.borderTopRightRadius = r;
            _achCard.style.borderBottomLeftRadius = r; _achCard.style.borderBottomRightRadius = r;

            var text = Theme != null ? Theme.TextColor : Color.white;
            _achTitle.text = "достижение";
            _achTitle.style.color = new Color(text.r, text.g, text.b, 0.6f);
            _achTitle.style.fontSize = (Theme != null ? Theme.BodyFontSize : 30) * 0.62f;
            _achName.text = title;
            _achName.style.color = text;
            _achName.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
            if (Theme != null)
            {
                LvnFonts.Apply(_achTitle, Theme.Font);
                LvnFonts.Apply(_achName, Theme.Font);
            }

            _achCard.style.display = DisplayStyle.Flex;
            _achHide?.Pause();
            _achHide = _achCard.schedule.Execute(() =>
            {
                if (_achCard != null) _achCard.style.display = DisplayStyle.None;
            }).StartingIn(3000);

            LvnPlayer.Log?.Invoke($"[достижение] {id}: {title}");
        }
    }
}
