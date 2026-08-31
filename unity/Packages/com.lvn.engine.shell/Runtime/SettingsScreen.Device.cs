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
        /// <summary>
        /// ИГРА ЦЕЛИКОМ — понятная строка вместо загадочной кнопки.
        ///
        /// <para>Было: заголовок «Игра целиком» и кнопка «Докачать ≈111 МБ» —
        /// игрок видел число, но не понимал ни что скачается, ни зачем, ни что
        /// будет, если не скачивать (снимок партнёра 28.08). Теперь строка
        /// отвечает на три вопроса сразу: сколько уже на устройстве, сколько
        /// осталось и что даёт скачивание. Пока идёт закачка — полоса и
        /// мегабайты, а не замершая кнопка.</para>
        /// </summary>
        private VisualElement StorageRow()
        {
            var box = new VisualElement();

            var status = new Label("…");
            status.style.color = _dim;
            status.style.fontSize = LvnTokens.TextSm;
            status.style.whiteSpace = WhiteSpace.Normal;
            status.style.marginBottom = 8;
            box.Add(status);

            // Полоса: «сколько уже у меня» видно глазом, а не арифметикой.
            var track = Lvn.UI.LvnStyler.Track(new VisualElement(), 8f);
            track.style.marginBottom = 12;
            track.style.display = DisplayStyle.None;
            var fill = Lvn.UI.LvnStyler.Fill(new VisualElement(), 4f, _accent);
            fill.style.height = 8;
            fill.style.width = new Length(0, LengthUnit.Percent);
            track.Add(fill);
            box.Add(track);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.flexWrap = Wrap.Wrap;
            var btn = new Button { text = "…" };
            StyleValueButton(btn, true);
            btn.style.marginRight = 8;
            btn.style.marginBottom = 8;
            btn.SetEnabled(false);
            buttons.Add(btn);
            var erase = Lvn.UI.LvnRedress.Bind(new Button(), () => LvnWords.Of("device.erase", "Erase"));
            StyleValueButton(erase, false);
            erase.style.marginBottom = 8;
            erase.style.display = DisplayStyle.None;
            buttons.Add(erase);
            box.Add(buttons);

            bool downloaded = false;
            IVisualElementScheduledItem ticker = null;

            void ShowBar(long got, long total)
            {
                track.style.display = total > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                if (total <= 0) return;
                // Через дом полосы: она доезжает до новой доли, а не прыгает
                // ступеньками раз в треть секунды.
                ScreenUi.SetFill(fill, (float)got / total);
            }

            async Task RefreshAsync()
            {
                ticker?.Pause();
                var (missing, count, used) = await StorageInfo();
                downloaded = count == 0;
                long total = used + missing;
                ShowBar(used, total);
                erase.style.display = ClearDownloads != null && used > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                if (downloaded)
                {
                    // Всё на устройстве — говорим именно это, а не «скачано».
                    status.text = LvnWords.Of("device.stored_all",
                        "The whole game is on this device ({0}) — it plays without the internet.",
                        Lvn.Content.LvnBytes.Short(used));
                    btn.style.display = DisplayStyle.None;
                }
                else
                {
                    btn.style.display = DisplayStyle.Flex;
                    status.text = used > 0
                        ? LvnWords.Of("device.partial",
                            "{0} on the device, {1} left. Chapters load as you read; download them up front to play offline.",
                            Lvn.Content.LvnBytes.Short(used), Lvn.Content.LvnBytes.Short(missing))
                        : LvnWords.Of("device.nothing_yet",
                            "Nothing downloaded yet — chapters load as you read. Download {0} up front to play offline.",
                            Lvn.Content.LvnBytes.Short(missing));
                    btn.text = (used > (8L << 20) ? LvnWords.Of("device.finish", "Finish download") : LvnWords.Of("device.download", "Download"))
                        + " " + Lvn.Content.LvnBytes.Short(missing);
                    btn.SetEnabled(true);
                }
            }

            btn.clicked += () =>
            {
                if (downloaded) return;
                btn.SetEnabled(false);
                Lvn.LvnAsync.Fire(DownloadAll(), "DownloadAll");
                // Живой прогресс: полоса и мегабайты, пока батч активен.
                ticker = box.schedule.Execute(() =>
                {
                    var p = DownloadProgress?.Invoke() ?? (0, 0, false);
                    if (p.active)
                    {
                        long expect = System.Math.Max(p.expected, p.received);
                        status.text = LvnWords.Of("device.downloading", "downloading… {0}",
                            Lvn.Content.LvnBytes.Short(p.received) + " / " + Lvn.Content.LvnBytes.Short(expect));
                        ShowBar(p.received, expect);
                    }
                    // Батч кончился — пересчёт состояния сам же и усыпит
                    // отсчёт первой своей строкой.
                    else LvnAsync.Fire(RefreshAsync(), "SettingsRefresh");
                }).Every(500);
            };

            erase.clicked += () =>
            {
                // Через дом занятости: очистка ждёт диск, и сорванное ожидание
                // оставляло кнопку мёртвой до перезахода в настройки.
                LvnAsync.Fire(Lvn.UI.LvnBusy.RunAsync(erase, Run, busyText: null,
                    releaseOnSuccess: false, what: "ClearDownloads"), "ClearDownloads");
                async Task Run() { await ClearDownloads(); await RefreshAsync(); }
            };

            LvnAsync.Fire(RefreshAsync(), "SettingsRefresh");
            return WideRow(LvnWords.Of("settings.full_game", "The whole game"), null, box);
        }

        // Качество арта: авто-режим движка против ручного пресета конкурентов —
        // но ручка экономии полезна на дорогом трафике.
        private VisualElement ArtQualityRow()
        {
            bool auto = string.IsNullOrEmpty(LvnPrefs.ArtQuality);
            string Current() => string.IsNullOrEmpty(LvnPrefs.ArtQuality)
                ? Lvn.UI.Screens.NovelApp.EffectiveArtQuality()
                : LvnPrefs.ArtQuality;
            // Через дом рядов: третья ступень («1K») уезжала за край экрана —
            // строка настроек вбок не прокручивается, и варианта для игрока
            // просто не существовало (TR-55).
            return WideRow(LvnWords.Of("settings.art_quality", "Art quality"),
                (auto ? LvnWords.Of("settings.art_quality_auto", "Picked for your screen automatically. ") : "")
                + LvnWords.Of("settings.art_quality_hint", "A lower step means less traffic and memory. Anything downloaded re-fetches itself in the new quality."),
                Lvn.UI.LvnSegment.Of(
                    new[] { ("2k", "2K"), ("1440", "1440p"), ("1k", "1K") },
                    o => o.Item2,
                    o => Current() == o.Item1,
                    o => LvnPrefs.ArtQuality = o.Item1,
                    StyleValueButton, alignEnd: false));
        }

        private VisualElement FpsRow()
        {
            return WideRow(LvnWords.Of("settings.frame_rate", "Frame rate"),
                LvnWords.Of("settings.frame_rate_hint", "30 fps saves battery; 60 animates smoother"),
                Lvn.UI.LvnSegment.Of(new[] { 30, 60 },
                    fps => fps.ToString(),
                    fps => (LvnPrefs.TargetFps == 30) == (fps == 30),
                    fps => LvnPrefs.TargetFps = fps,
                    StyleValueButton, alignEnd: false));
        }

        // "Restore purchases": re-syncs the wallet from the server, which re-grants
        // any purchases the account already owns. (Real platform restore is host-side.)
        private VisualElement RestoreRow()
        {
            var row = RowEx(LvnWords.Of("settings.restore_purchases", "Restore purchases"),
                LvnWords.Of("settings.restore_purchases_hint", "Tap if purchases went missing after a reinstall"));
            // Надпись читает СОСТОЯНИЕ: покой → «Восстановить», ожидание →
            // многоточие, кончилось → «Готово». Назначь её руками — и смена
            // языка на открытых настройках вернула бы «Восстановить» кнопке,
            // которая уже отработала.
            int step = 0; // 0 покой, 1 ждём, 2 готово
            var btn = Lvn.UI.LvnRedress.Bind(new Button(), () =>
                step == 1 ? "…"
              : step == 2 ? LvnWords.Of("common.done", "Done")
              : LvnWords.Of("device.restore", "Restore"));
            StyleValueButton(btn, false);
            btn.clicked += () =>
            {
                LvnAsync.Fire(Lvn.Services.LvnWallet.RefreshAsync(), "Refresh");
                step = 1;
                Lvn.UI.LvnRedress.Refresh(btn);
                btn.schedule.Execute(() => { step = 2; Lvn.UI.LvnRedress.Refresh(btn); })
                   .ExecuteLater(LvnMotion.Ms(LvnMotion.Notice));
            };
            row.Add(btn);
            return row;
        }
    }
}
