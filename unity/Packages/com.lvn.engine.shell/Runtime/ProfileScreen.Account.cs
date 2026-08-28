using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// АККАУНТ В ПРОФИЛЕ — часть <see cref="ProfileScreen"/>: идентификатор
    /// игрока, ссылка в настройки и удаление аккаунта со взведённым
    /// подтверждением (стор-требование).
    /// </summary>
    public sealed partial class ProfileScreen
    {
        // Ссылка на настройки: звук/язык/загрузку ищут в профиле — дадим путь.
        private VisualElement SettingsLink()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.backgroundColor = new Color(LvnTokens.Surface.r, LvnTokens.Surface.g, LvnTokens.Surface.b, 0.88f);
            var rowEdge = LvnTokens.Border;
            LvnChrome.Border(row, new Color(rowEdge.r, rowEdge.g, rowEdge.b, rowEdge.a * 0.64f), 1f);
            LvnChrome.Round(row, LvnTokens.RadiusSm);
            row.style.marginTop = 6; row.style.marginBottom = 10;
            row.style.paddingTop = 14; row.style.paddingBottom = 14;
            row.style.paddingLeft = 16; row.style.paddingRight = 16;
            var col = new VisualElement();
            col.style.flexGrow = 1;
            var lbl = new Label(LvnWords.Of("settings.title", "Settings"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = 24;
            col.Add(lbl);
            var hint = new Label(LvnWords.Of("settings.hint", "Sound, story language and full download"));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = 19;
            hint.style.marginTop = 2;
            col.Add(hint);
            row.Add(col);
            var arrow = new Label("›");
            arrow.style.color = LvnTokens.Accent;
            arrow.style.fontSize = 30;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(arrow);
            row.RegisterCallback<ClickEvent>(_ => { Close(); OnOpenSettings?.Invoke(); });
            return row;
        }

        // «Удалить аккаунт»: приглушённая строка с подтверждением в два нажатия
        // прямо в кнопке — отдельный диалог тут был бы тяжелее самого действия.
        private VisualElement DeleteAccountRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.backgroundColor = LvnTokens.Surface;
            var deleteEdge = LvnTokens.Border;
            LvnChrome.Border(row, new Color(deleteEdge.r, deleteEdge.g, deleteEdge.b, deleteEdge.a * 0.55f), 1f);
            LvnChrome.Round(row, LvnTokens.RadiusSm);
            row.style.marginBottom = 10;
            row.style.paddingTop = 14; row.style.paddingBottom = 14;
            row.style.paddingLeft = 16; row.style.paddingRight = 16;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.marginRight = 10;
            var lbl = new Label(LvnWords.Of("account.delete", "Delete account"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = 24;
            col.Add(lbl);
            var hint = new Label(LvnWords.Of("account.delete_hint", "Erases progress, purchases and saves. Forever."));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = 19;
            hint.style.marginTop = 2;
            hint.style.whiteSpace = WhiteSpace.Normal;
            col.Add(hint);
            row.Add(col);

            var danger = new Color(0.86f, 0.28f, 0.32f);
            var btn = new Button { text = LvnWords.Of("account.delete_do", "Delete") };
            btn.style.fontSize = 20;
            btn.style.paddingTop = 10; btn.style.paddingBottom = 10;
            btn.style.paddingLeft = 16; btn.style.paddingRight = 16;
            btn.style.color = danger;
            btn.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(btn);
            LvnChrome.Round(btn, LvnTokens.RadiusSm);

            bool armed = false;
            btn.clicked += () =>
            {
                if (!armed)
                {
                    // Первое нажатие только взводит; через 4 с кнопка остывает.
                    armed = true;
                    btn.text = LvnWords.Of("account.delete_sure", "Really delete?");
                    btn.style.backgroundColor = danger;
                    btn.style.color = Color.white;
                    btn.schedule.Execute(() =>
                    {
                        if (!armed) return;
                        armed = false;
                        btn.text = LvnWords.Of("account.delete_do", "Delete");
                        btn.style.backgroundColor = LvnTokens.Faint;
                        btn.style.color = danger;
                    }).ExecuteLater(LvnMotion.Ms(ArmedWindowMs));
                    return;
                }
                armed = false;
                btn.SetEnabled(false);
                btn.text = LvnWords.Of("account.deleting", "Deleting…");
                LvnAsync.Fire(RunDeleteAsync(btn, danger), "DeleteAccount");
            };
            row.Add(btn);
            return row;
        }

        private async Task RunDeleteAsync(Button btn, Color danger)
        {
            bool ok = false;
            try { ok = await OnDeleteAccount(); }
            catch (Exception e) { Debug.LogWarning($"[profile] удаление аккаунта: {e.Message}"); }
            if (ok) { Close(); return; }
            btn.SetEnabled(true);
            btn.style.backgroundColor = LvnTokens.Faint;
            btn.style.color = danger;
            btn.text = LvnWords.Of("account.delete_do", "Delete");
            LvnMotion.FlashText(btn, Lvn.Content.LvnOfflineText.TryLater, LvnMotion.NoticeLong);
        }

        // ── Section 6: footer (UID + copy) ─────────────────────────────────
        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            footer.style.justifyContent = Justify.SpaceBetween;
            footer.style.marginTop = 8;
            footer.style.paddingTop = 12;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = LvnTokens.Border;

            var id = string.IsNullOrEmpty(Uid) ? "u_unknown" : Uid;
            var idLabel = new Label($"ID: {Shorten(id)}");
            idLabel.style.color = LvnTokens.TextDim;
            idLabel.style.fontSize = 20;
            idLabel.style.flexGrow = 1;
            footer.Add(idLabel);

            var copy = new Button { text = LvnWords.Of("settings.copy", "Copy") };
            copy.style.fontSize = 20;
            copy.style.paddingTop = 10;
            copy.style.paddingBottom = 10;
            copy.style.paddingLeft = 16;
            copy.style.paddingRight = 16;
            copy.style.color = LvnTokens.OnAccent;
            copy.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.ClearBorder(copy);
            LvnChrome.Round(copy, LvnTokens.RadiusSm);
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = id;
                LvnMotion.FlashText(copy, "Скопировано");
            };
            footer.Add(copy);

            return footer;
        }

        private static string Shorten(string id)
            => id != null && id.Length > 12 ? id.Substring(0, 12) + "…" : id;
    }
}
