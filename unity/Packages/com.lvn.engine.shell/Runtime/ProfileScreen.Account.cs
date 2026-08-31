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
            LvnChrome.BorderSoft(row, 0.64f);
            LvnChrome.Round(row, LvnTokens.RadiusSm);
            row.style.marginTop = 6; row.style.marginBottom = 10;
            row.style.paddingTop = 14; row.style.paddingBottom = 14;
            row.style.paddingLeft = 16; row.style.paddingRight = 16;
            var col = new VisualElement();
            col.style.flexGrow = 1;
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("settings.title", "Settings"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = Lvn.UI.LvnFonts.Size(24f);
            col.Add(lbl);
            var hint = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("settings.hint", "Sound, story language and full download"));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = Lvn.UI.LvnFonts.Size(19f);
            hint.style.marginTop = 2;
            col.Add(hint);
            row.Add(col);
            var arrow = new Label("›");
            arrow.style.color = LvnTokens.Accent;
            arrow.style.fontSize = Lvn.UI.LvnFonts.Size(30f);
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
            LvnChrome.BorderSoft(row, 0.64f);
            LvnChrome.Round(row, LvnTokens.RadiusSm);
            row.style.marginBottom = 10;
            row.style.paddingTop = 14; row.style.paddingBottom = 14;
            row.style.paddingLeft = 16; row.style.paddingRight = 16;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.marginRight = 10;
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("account.delete", "Delete account"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = Lvn.UI.LvnFonts.Size(24f);
            col.Add(lbl);
            var hint = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("account.delete_hint", "Erases progress, purchases and saves. Forever."));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = Lvn.UI.LvnFonts.Size(19f);
            hint.style.marginTop = 2;
            hint.style.whiteSpace = WhiteSpace.Normal;
            col.Add(hint);
            row.Add(col);

            var danger = new Color(0.86f, 0.28f, 0.32f);
            // НАДПИСЬ ЧИТАЕТ СОСТОЯНИЕ, А НЕ НАЗНАЧАЕТСЯ ПО ШАГАМ. Со сменой
            // языка привязка перечитывает источник: назначь надпись руками — и
            // взведённая кнопка вернула бы вид «Удалить», оставшись взведённой.
            // Следующее нажатие удалило бы аккаунт без переспроса.
            bool armed = false;
            var btn = Lvn.UI.LvnRedress.Bind(new Button(), () => armed
                ? LvnWords.Of("account.delete_sure", "Really delete?")
                : LvnWords.Of("account.delete_do", "Delete"));
            btn.style.fontSize = Lvn.UI.LvnFonts.Size(20f);
            btn.style.paddingTop = 10; btn.style.paddingBottom = 10;
            btn.style.paddingLeft = 16; btn.style.paddingRight = 16;
            LvnStyler.Plate(btn, LvnTokens.Faint, danger, LvnTokens.RadiusSm);

            btn.clicked += () =>
            {
                if (!armed)
                {
                    // Первое нажатие только взводит; через 4 с кнопка остывает.
                    armed = true;
                    Lvn.UI.LvnRedress.Refresh(btn);
                    btn.style.backgroundColor = danger;
                    btn.style.color = Color.white;
                    btn.schedule.Execute(() =>
                    {
                        if (!armed) return;
                        armed = false;
                        Lvn.UI.LvnRedress.Refresh(btn);
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
            idLabel.style.fontSize = Lvn.UI.LvnFonts.Size(20f);
            idLabel.style.flexGrow = 1;
            footer.Add(idLabel);

            var copy = Lvn.UI.LvnRedress.Bind(new Button(), () => LvnWords.Of("settings.copy", "Copy"));
            copy.style.fontSize = Lvn.UI.LvnFonts.Size(20f);
            copy.style.paddingTop = 10;
            copy.style.paddingBottom = 10;
            copy.style.paddingLeft = 16;
            copy.style.paddingRight = 16;
            LvnStyler.Primary(copy, LvnTokens.RadiusSm);
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = id;
                LvnMotion.FlashText(copy, LvnWords.Of("common.copied", "Copied"));
            };
            footer.Add(copy);

            return footer;
        }

        private static string Shorten(string id)
            => Lvn.Content.LvnClip.Id(id);
    }
}
