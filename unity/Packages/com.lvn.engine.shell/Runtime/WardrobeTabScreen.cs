using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВКЛАДКА ГАРДЕРОБА (концепция Ильи 26.08): героиня общего слоя меню
    /// стоит в центре внимания, а эта страница рисует только UI ВОКРУГ неё —
    /// оси и варианты из манифеста. ГАРДЕРОБ ОДИН: данные и надевание — тот же
    /// LvnWardrobe, что и у сценического шита в игре; здесь лишь меню-вид.
    /// Покупки платных вариантов остаются в сценическом шите (этап 2 —
    /// перенести цены и сюда).
    /// </summary>
    public sealed class WardrobeTabScreen : LvnOverlayScreen
    {
        private readonly LvnManifest _manifest;
        private readonly string _entity;
        private readonly VisualElement _panel;

        public WardrobeTabScreen(LvnManifest manifest)
        {
            _manifest = manifest;
            _entity = manifest?.ui?.wardrobe?.entity;
            ScreenUi.Stretch(this);
            style.backgroundColor = Color.clear;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            pickingMode = PickingMode.Ignore;

            var title = new Label(manifest?.ui?.wardrobe?.title ?? "Гардероб");
            LvnChrome.Heading(title);
            title.pickingMode = PickingMode.Ignore;
            title.style.position = Position.Absolute;
            title.style.top = 108; title.style.left = 20;
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 40;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(title);

            // Панель осей — снизу, над нижним меню; героиня видна за ней.
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0; _panel.style.right = 0;
            _panel.style.bottom = 132;
            _panel.style.paddingLeft = 20; _panel.style.paddingRight = 20;
            Add(_panel);
        }

        protected override void OnOpening() => Rebuild();

        public void Rebuild()
        {
            _panel.Clear();
            if (string.IsNullOrEmpty(_entity)
                || _manifest?.sprites == null
                || !_manifest.sprites.TryGetValue(_entity, out var def)
                || def?.wardrobe == null) return;

            foreach (var kv in def.wardrobe)
            {
                string axis = kv.Key;
                var slot = kv.Value;
                if (slot?.items == null || slot.items.Count == 0) continue;

                var card = new VisualElement();
                var bg = LvnTokens.PanelBg;
                card.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.82f);
                LvnChrome.Edge(card);
                LvnChrome.Round(card, 14f);
                card.style.paddingTop = 10; card.style.paddingBottom = 12;
                card.style.paddingLeft = 14; card.style.paddingRight = 14;
                card.style.marginTop = 10;
                _panel.Add(card);

                var name = new Label(slot.name ?? axis);
                name.pickingMode = PickingMode.Ignore;
                name.style.color = LvnTokens.TextDim;
                name.style.fontSize = 19;
                name.style.marginBottom = 8;
                card.Add(name);

                var row = new ScrollView(ScrollViewMode.Horizontal);
                row.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                row.style.flexDirection = FlexDirection.Row;
                card.Add(row);

                var eq = LvnWardrobe.Equipped(_entity);
                string current = eq != null && eq.TryGetValue(axis, out var cv) ? cv : null;
                if (string.IsNullOrEmpty(current)
                    && def.defaults != null && def.defaults.TryGetValue(axis, out var dv)) current = dv;

                foreach (var item in slot.items)
                {
                    if (item == null || string.IsNullOrEmpty(item.value)) continue;
                    bool on = item.value == current;
                    var pill = new Button { text = item.name ?? item.value };
                    pill.style.height = 46;
                    pill.style.fontSize = 21;
                    pill.style.marginRight = 8;
                    pill.style.paddingLeft = 16; pill.style.paddingRight = 16;
                    pill.style.color = on ? LvnTokens.OnAccent : LvnTokens.Text;
                    pill.style.backgroundColor = on ? LvnTokens.Accent : LvnTokens.Faint;
                    LvnChrome.ClearBorder(pill);
                    LvnChrome.Round(pill, 14f);
                    var value = item.value;
                    pill.clicked += () =>
                    {
                        LvnWardrobe.Equip(_entity, axis, value); // кукла меню обновится сама
                        Rebuild();
                    };
                    row.Add(pill);
                }
            }
        }
    }
}
