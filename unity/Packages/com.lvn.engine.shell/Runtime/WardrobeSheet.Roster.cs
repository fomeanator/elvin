using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КОГО ОДЕВАЕМ — список персонажей гардероба и переключение между ними.
    ///
    /// <para>Гардероб открывается и на героине из меню, и на том, кто стоит в
    /// текущей сцене. Список — это не украшение: пока его не было, «одеть
    /// другого» означало закрыть лист и открыть его иначе, и половина
    /// персонажей была недоступна вовсе.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        private List<(string id, string name)> _roster;

        /// <summary>Give the sheet a character roster (menu/hub mode). Null or a
        /// single entry hides the pills. Call before ShowAsync — cleared state
        /// persists on the shared instance otherwise.</summary>
        public void SetRoster(List<(string id, string name)> roster) => _roster = roster;

        private void RebuildRoster()
        {
            if (_rosterRow == null) return;
            _rosterRow.Clear();
            int shown = 0;
            if (_roster != null && _roster.Count > 1)
            {
                foreach (var (id, name) in _roster)
                {
                    if (OnlySeen && id != _entity && !HasAnyCollected(id)) continue;
                    var pid = id;
                    // Подпись знает свой источник: имя персонажа приходит из
                    // каталога и переводится словарём, поэтому при смене языка
                    // её перечитает дом — раньше баблики не реагировали ни на
                    // что (снимок Ильи 28.08).
                    var b = new Button(() => SwitchTo(pid));
                    Lvn.UI.LvnRedress.Bind(b, () => Lvn.Content.LvnWords.Name("actor", pid, name));
                    b.style.height = LvnTokens.Touch;
                    LvnAir.PadX(b, LvnTokens.Space2);
                    LvnAir.MarginX(b, 0);
                    b.style.marginBottom = LvnTokens.Space1;
                    b.style.fontSize = LvnTokens.TextXs;
                    bool active = pid == _entity;
                    SkinButton(b, active);
                    LvnStyler.Chosen(b, active, _accent);
                    _rosterRow.Add(b);
                    shown++;
                }
            }
            _rosterRow.style.display = shown > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SwitchTo(string id)
        {
            if (string.IsNullOrEmpty(id) || id == _entity) return;
            var from = _entity;
            LvnWardrobe.ClearPreview(from); // the outgoing look blends back
            OnCharacterPicked?.Invoke(from, id);
            BuildFor(id);
            RefreshBalances();
        }

        // Does this entity have anything to show in collection mode? Mirrors
        // Items()' Encountered rule without switching the sheet to it.
        private bool HasAnyCollected(string id)
        {
            if (_manifest?.sprites == null || !_manifest.sprites.TryGetValue(id, out var d)
                || d?.wardrobe == null) return false;
            foreach (var kv in d.wardrobe)
                if (kv.Value?.items != null)
                    foreach (var it in kv.Value.items)
                        if (it != null && !string.IsNullOrEmpty(it.value) && Encountered(id, kv.Key, it.value))
                            return true;
            return false;
        }
    }
}
