using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// СОХРАНЕНИЯ — часть <see cref="StageMenu"/>: список слотов, запись,
    /// чтение и подтверждение перезаписи.
    /// </summary>
    public sealed partial class StageMenu
    {
        private void ShowSlots(bool saveMode)
        {
            var p = Panel(saveMode ? L("save", "Save") : L("load", "Load"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var all = LvnSaveStore.Slots(_stage.SaveTitleId);

            // Engine-owned slots appear in load mode only: the rolling autosave
            // and the one-tap quick save.
            if (!saveMode && all.TryGetValue(LvnSaveStore.AutoSlot, out var auto) && auto?.Snap != null)
                scroll.Add(SlotRow(L("autosave", "Autosave"), auto, () => TryLoad(LvnSaveStore.AutoSlot)));
            if (!saveMode && all.TryGetValue(QuickSlot, out var quick) && quick?.Snap != null)
                scroll.Add(SlotRow(L("quick_slot", "Quick save"), quick, () => TryLoad(QuickSlot), thumbSlot: QuickSlot));

            for (int i = 0; i < SlotCount; i++)
            {
                var name = "slot" + (i + 1);
                all.TryGetValue(name, out var slot);
                var label = L("slot", "Slot") + " " + (i + 1);
                if (saveMode)
                {
                    var occupied = slot?.Snap != null; // an occupied slot asks before it's lost
                    scroll.Add(SlotRow(label, slot, () =>
                    {
                        if (occupied) ConfirmOverwrite(label, name);
                        // Успех — обновляем список; отказ хранилища сообщаем:
                        // «нажал и ничего не произошло» неотличимо от «сохранено».
                        else if (_stage.SaveToSlot(name)) ShowSlots(true);
                        else SaveFailedNotice();
                    }, thumbSlot: name));
                }
                else
                    scroll.Add(SlotRow(label, slot, () => TryLoad(name), enabled: _stage.CanLoadSlot(name), thumbSlot: name));
            }
        }

        // Overwriting a save is the one destructive tap in the whole menu — make
        // it a two-step: a small panel naming the slot, confirm or go back.
        private void ConfirmOverwrite(string label, string slotName)
        {
            var p = Panel(L("save", "Save"));
            var msg = Text(string.Format(L("overwrite_q", "Overwrite {0}?"), label), 26, FontStyle.Normal);
            msg.style.marginBottom = 12;
            p.Add(msg);
            p.Add(Item(L("overwrite", "Overwrite"), () =>
            {
                if (_stage.SaveToSlot(slotName)) ShowSlots(true);
                else SaveFailedNotice();
            }));
            p.Add(Item(L("cancel", "Cancel"), () => ShowSlots(true)));
        }

        private void TryLoad(string slot) => LvnAsync.Fire(TryLoadAsync(slot), "TryLoad");

        private async Task TryLoadAsync(string slot)
        {
            // Same-chapter slots restore in place; another chapter's slot routes
            // through the host (fetch that chapter's script, play, restore).
            //
            // Отказ был неотличим от «не нажалось»: меню оставалось открытым и
            // молчало. А причины настоящие — снимок пуст, глава сейва не
            // открывается этим хостом, скрипт не доехал; игрок же видел только
            // кнопку, которая «не работает».
            bool ok;
            // Упавшая загрузка — тот же отказ для игрока, что и вернувшая false:
            // без этого исключение уносило и результат, и объяснение.
            try { ok = await _stage.LoadFromSlotAsync(slot); }
            catch { LoadFailedNotice(); throw; }
            if (ok) Close();
            else LoadFailedNotice();
        }

        /// <summary>«Загрузить не вышло» — вторая половина той же честности, что
        /// и у отказа записи: игрок должен отличать «не получилось» от «не
        /// нажалось».</summary>
        private void LoadFailedNotice()
        {
            var p = Panel(L("load", "Load"));
            var msg = Text(L("load_failed", "Could not load this save."), 26, FontStyle.Normal);
            msg.style.marginBottom = 12;
            p.Add(msg);
            p.Add(Item(L("close", "Close"), () => ShowSlots(false)));
        }

        private VisualElement SlotRow(string label, LvnSaveSlot slot, Action onClick, bool enabled = true,
            string thumbSlot = null)
        {
            var row = new Button(onClick);
            row.style.height = 56;
            row.style.marginBottom = 6;
            var tint = _theme.MenuTextColor;
            row.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.06f);
            row.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.style.paddingLeft = 12;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            LvnChrome.Round(row, Mathf.Max(4f, _theme.MenuCornerRadius - 4f));
            LvnChrome.ClearBorder(row);
            row.SetEnabled(enabled);

            // The saved scene's screenshot, when one exists for this slot.
            var thumb = thumbSlot != null && slot?.Snap != null
                ? LvnSaveStore.LoadThumb(_stage.SaveTitleId, thumbSlot) : null;
            if (thumb != null)
            {
                _thumbs.Add(thumb);
                var img = new Image { image = thumb, scaleMode = ScaleMode.ScaleAndCrop, name = "slot-thumb" };
                img.style.width = 80;
                img.style.height = 45;
                img.style.marginRight = 10;
                img.style.flexShrink = 0;
                LvnChrome.Round(img, 4f);
                row.Add(img);
            }

            var text = new VisualElement();
            text.style.flexDirection = FlexDirection.Column;
            text.style.justifyContent = Justify.Center;
            text.style.flexGrow = 1;
            string when = slot?.Snap == null ? L("empty", "— empty —")
                : LvnTimeWords.Stamp(slot.SavedAtUnixMs);
            text.Add(Text(label + "   " + when, 24, FontStyle.Bold));
            if (!string.IsNullOrEmpty(slot?.Preview))
                text.Add(Text("«" + Trunc(slot.Preview, 46) + "»", 20, FontStyle.Italic, dim: true));
            row.Add(text);
            return row;
        }
    }
}
