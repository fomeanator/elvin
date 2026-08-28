using System;
using System.Collections.Generic;
using Lvn.Services;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// УСТРОЙСТВО — часть <see cref="SettingsScreen"/>: место на диске, ступень
    /// качества арта, потолок кадров и восстановление покупок. Всё, что про
    /// железо и его ресурсы, а не про вкус игрока.
    /// </summary>
    public sealed partial class SettingsScreen
    {
        // «Скачать всю игру»: строка-автомат — оценка → загрузка с живыми
        // мегабайтами → «Скачано» с кнопкой удаления. Играть можно и без неё
        // (стриминг), кнопка — для самолёта и плохой сети.
        private VisualElement StorageRow()
        {
            var row = RowEx("Игра целиком",
                "Скачайте истории заранее, чтобы играть без интернета. " +
                "Пока не скачано — главы загружаются по мере чтения.");
            var status = new Label("…");
            status.style.color = _dim;
            status.style.fontSize = 13;
            status.style.marginRight = 8;
            row.Add(status);
            var btn = new Button { text = "…" };
            StyleValueButton(btn, true);
            btn.SetEnabled(false);
            row.Add(btn);

            bool downloaded = false;
            IVisualElementScheduledItem ticker = null;

            async Task RefreshAsync()
            {
                ticker?.Pause();
                var (missing, count, used) = await StorageInfo();
                downloaded = count == 0;
                if (downloaded)
                {
                    status.text = LvnWords.Of("device.stored", "downloaded · {0} MB used", used >> 20);
                    btn.text = LvnWords.Of("device.erase", "Erase");
                    btn.SetEnabled(ClearDownloads != null);
                }
                else
                {
                    status.text = "";
                    // «Докачать», когда на диске уже что-то живёт: игрок
                    // скачал почти всё — не предлагать ему «Скачать» заново.
                    btn.text = (used > (8L << 20) ? LvnWords.Of("device.finish", "Finish download") : LvnWords.Of("device.download", "Download"))
                        + $" ≈{System.Math.Max(1, missing >> 20)} МБ";
                    btn.SetEnabled(true);
                }
            }

            btn.clicked += () =>
            {
                if (!downloaded)
                {
                    btn.SetEnabled(false);
                    _ = DownloadAll();
                    // Живой прогресс в мегабайтах, пока батч активен.
                    ticker = row.schedule.Execute(() =>
                    {
                        var p = DownloadProgress?.Invoke() ?? (0, 0, false);
                        if (p.active)
                            status.text = LvnWords.Of("device.downloading", "downloading… {0}", $"{p.received >> 20} / {System.Math.Max(p.expected, p.received) >> 20} " + LvnWords.Of("unit.mb", "MB"));
                        else
                            LvnAsync.Fire(RefreshAsync(), "SettingsRefresh");
                    }).Every(500);
                }
                else
                {
                    btn.SetEnabled(false);
                    LvnAsync.Fire(Run(), "ClearDownloads");
                    async Task Run() { await ClearDownloads(); await RefreshAsync(); }
                }
            };

            LvnAsync.Fire(RefreshAsync(), "SettingsRefresh");
            return row;
        }

        // Качество арта: авто-режим движка против ручного пресета конкурентов —
        // но ручка экономии полезна на дорогом трафике.
        private VisualElement ArtQualityRow()
        {
            bool auto = string.IsNullOrEmpty(LvnPrefs.ArtQuality);
            var row = RowEx("Качество арта",
                (auto ? "Подобрано под ваш экран автоматически. " : "")
                + "Ниже ступень — меньше трафика и памяти. Скачанное "
                + "перекачается в новом качестве само");
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);
            var buttons = new List<(string q, Button b)>();
            string Current() => string.IsNullOrEmpty(LvnPrefs.ArtQuality)
                ? Lvn.UI.Screens.NovelApp.EffectiveArtQuality()
                : LvnPrefs.ArtQuality;
            void Highlight()
            {
                foreach (var (q, b) in buttons) StyleValueButton(b, Current() == q);
            }
            foreach (var (q, label) in new[] { ("2k", "2K"), ("1440", "1440p"), ("1k", "1K") })
            {
                var btn = new Button { text = label };
                btn.style.marginLeft = 6;
                var quality = q;
                btn.clicked += () => { LvnPrefs.ArtQuality = quality; Highlight(); };
                buttons.Add((q, btn));
                seg.Add(btn);
            }
            Highlight();
            return row;
        }

        private VisualElement FpsRow()
        {
            var row = RowEx("Кадровая частота",
                "30 кадров — дольше живёт батарея; 60 — плавнее анимации");
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);
            Button f30 = null, f60 = null;
            void Highlight()
            {
                StyleValueButton(f30, LvnPrefs.TargetFps == 30);
                StyleValueButton(f60, LvnPrefs.TargetFps != 30);
            }
            f30 = new Button { text = "30" };
            f30.style.marginLeft = 6;
            f30.clicked += () => { LvnPrefs.TargetFps = 30; Highlight(); };
            f60 = new Button { text = "60" };
            f60.style.marginLeft = 6;
            f60.clicked += () => { LvnPrefs.TargetFps = 60; Highlight(); };
            seg.Add(f30); seg.Add(f60);
            Highlight();
            return row;
        }

        // "Restore purchases": re-syncs the wallet from the server, which re-grants
        // any purchases the account already owns. (Real platform restore is host-side.)
        private VisualElement RestoreRow()
        {
            var row = RowEx("Восстановить покупки",
                "Если после переустановки пропали покупки — нажмите");
            var btn = new Button { text = LvnWords.Of("device.restore", "Restore") };
            StyleValueButton(btn, false);
            btn.clicked += () =>
            {
                LvnAsync.Fire(Lvn.Services.LvnWallet.RefreshAsync(), "Refresh");
                btn.text = "…";
                btn.schedule.Execute(() => btn.text = LvnWords.Of("common.done", "Done")).ExecuteLater(LvnMotion.Ms(LvnMotion.Notice));
            };
            row.Add(btn);
            return row;
        }
    }
}
