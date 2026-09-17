using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КУСКИ МАКЕТОВ СПИСКА, ДЕТАЛИ И СЮЖЕТА РЕАЛЬНОСТИ (Figma 17.09).
    ///
    /// <para>Три экрана нарисованы одним языком: светящаяся рамка с угловыми
    /// скобами, окно постера с тонкой кромкой, шестигранные плашки-кнопки,
    /// плашки жанров. Каждый экран мог бы собирать их сам из своих чисел — и
    /// три копии рамки разошлись бы на первой же правке арта. Здесь кусок
    /// назван один раз; экран говорит только, где он стоит.</para>
    ///
    /// <para>Краски — по макету, а не по роли темы: «золото» макета не равно
    /// <see cref="LvnTokens.Gold"/> темы, и подменять одно другим значило бы
    /// красить макет наугад.</para>
    /// </summary>
    internal static partial class LvnStageKit
    {
        /// <summary>Арт списка, детали и сюжета: грузится, когда экран открыт,
        /// а не бутом — главной он не нужен, и вуаль его не ждёт.</summary>
        public static readonly string[] ViewFiles =
        {
            "frame.png", "window.png", "ornament-top.png", "ornament-side.png",
            "btn-open.png", "btn-play.png", "btn-icon.png", "btn-row.png", "ribbon.png",
            "back.png", "close.png", "divider.png",
            "icon-bookmark.png", "icon-refresh.png", "icon-mail-read.png", "icon-mail-new.png",
            "icon-lock.png", "icon-check.png", "glyph-question.png",
        };

        /// <summary>Краски макета — по имени цвета в макете (hex из Figma).</summary>
        public static class Ink
        {
            public static readonly Color Gold   = new Color32(0xE5, 0xCC, 0x87, 255);   // заголовки, слова кнопок
            public static readonly Color Sand   = new Color32(0xBD, 0xA8, 0x6C, 255);   // эпоха на карточке, подзаголовок шапки
            public static readonly Color Ochre  = new Color32(0x99, 0x86, 0x54, 255);   // «Мир экспедиции», прочитанный ряд
            public static readonly Color Body   = new Color32(0xC5, 0xD7, 0xD9, 255);   // описание
            public static readonly Color Sky    = new Color32(0x93, 0xCA, 0xDA, 255);   // «Глава 5/12», плашка статуса
            public static readonly Color Cyan   = new Color32(0x83, 0xEF, 0xFC, 255);   // ход полосы, закрытый ряд, «Только новые»
            public static readonly Color Ice    = new Color32(0xC8, 0xF8, 0xFD, 255);   // «Показывать все»
            public static readonly Color Ribbon = new Color32(0xAC, 0xF6, 0xFF, 255);   // лента попапа, «Жанр истории:»
            public static readonly Color Teal   = new Color32(0x5B, 0xAD, 0xB7, 255);   // «Прочитано»
            public static readonly Color Grass  = new Color32(0x8F, 0xFC, 0x83, 255);   // «Новое сообщение!»
            public static readonly Color Mint   = new Color32(0xC6, 0xEB, 0xC2, 255);   // дата сообщения
            public static readonly Color Grey   = new Color32(0x9E, 0xAC, 0xAD, 255);   // «Заблокировано»
            public static readonly Color Edge   = new Color32(0x27, 0x89, 0xA3, 255);   // кромки, рамка плашки статуса
            public static readonly Color ChipBg = new Color32(0x00, 0x10, 0x1B, 255);   // подложка плашки статуса
            public static readonly Color Bg     = new Color32(0x05, 0x0D, 0x13, 255);   // тьма экрана
            public static readonly Color Plate  = new Color32(4, 38, 63, 207);     // подложка фильтра, 81 %
            public static readonly Color Veil   = new Color32(2, 7, 10, 217);      // затемнение попапа, 85 %
            // Байтами, а не разбором строки: это константы кода, а не авторские
            // цвета, и словарю имён тут делать нечего.
        }

        // ── рамки ────────────────────────────────────────────────────────────

        /// <summary>СВЕТЯЩАЯСЯ РАМКА на всё нутро хозяина, под содержимым:
        /// углы и кромки — из арта, середина — его градиент, растянутый.
        /// Орнаменты живут в самой рамке: верхний блик у левого верха, боковой
        /// — у правого края по центру, высотой по макету экрана
        /// (<paramref name="ornamentH"/>: 154 у карточки, 82 у ряда, 448 у листа).</summary>
        public static VisualElement Glow(VisualElement host, string skin, ILvnAssets assets,
                                         float ornamentH, int index = 0)
        {
            var f = LvnStageSkin.Glow;
            var frame = HollowFrame(host, SkinUrl(skin, "frame.png"), assets,
                                    f.ImageW, f.ImageH, f.CornerPx, f.PxPerDp(Bleed), index, solid: true);
            frame.name = "stage-glow";
            // Блик сверху: экспорт без запаса, оседлал верхнюю кромку.
            frame.Add(Art(SkinUrl(skin, "ornament-top.png"), assets,
                          D(Bleed) + D(32f), D(Bleed) - D(5f), D(86f), D(20f), bleed: 0f));
            // Боковой орнамент: выступает за правый край на 13, стоит по центру.
            var side = Art(SkinUrl(skin, "ornament-side.png"), assets, 0f, 0f, D(34f), D(ornamentH));
            side.style.left = StyleKeyword.Auto;
            side.style.right = -D(13f);
            side.style.top = Length.Percent(50f);
            side.style.translate = new Translate(0f, Length.Percent(-50f));
            frame.Add(side);
            return frame;
        }

        /// <summary>КРОМКА ОКНА ПОСТЕРА — поверх постера: тонкая бирюзовая
        /// линия с угловыми метками и свечением внутрь, без середины.</summary>
        public static VisualElement Window(VisualElement host, string skin, ILvnAssets assets)
        {
            var f = LvnStageSkin.Window;
            var frame = HollowFrame(host, SkinUrl(skin, "window.png"), assets,
                                    f.ImageW, f.ImageH, f.CornerPx, f.PxPerDp(Bleed),
                                    index: host.childCount, solid: false);
            frame.name = "stage-window";
            return frame;
        }

        /// <summary>Затемнения экрана списка: сверху под шапку, снизу под
        /// ленту — как «shadow layer» макета. Кладутся первыми, под всё.</summary>
        public static void Scrims(VisualElement host)
        {
            var top = new VisualElement { name = "stage-scrim-top", pickingMode = PickingMode.Ignore };
            top.style.position = Position.Absolute;
            top.style.left = 0; top.style.right = 0; top.style.top = 0; top.style.height = D(130f);
            top.style.backgroundImage = LvnBackdrop.Vertical(
                UiColor.WithAlpha(Ink.Bg, 0.9f), UiColor.WithAlpha(Ink.Bg, 0f), smooth: true);
            var bottom = new VisualElement { name = "stage-scrim-bottom", pickingMode = PickingMode.Ignore };
            bottom.style.position = Position.Absolute;
            bottom.style.left = 0; bottom.style.right = 0; bottom.style.bottom = 0; bottom.style.height = D(197f);
            bottom.style.backgroundImage = LvnBackdrop.Vertical(
                UiColor.WithAlpha(Ink.Bg, 0f), UiColor.WithAlpha(Ink.Bg, 0.95f), smooth: true);
            host.Insert(0, bottom);
            host.Insert(0, top);
        }

        // ── слова ────────────────────────────────────────────────────────────

        /// <summary>Строка макета: слева, в одну строку, кегль в dp макета.</summary>
        public static Label Line(Func<string> text, float dp, Color color, bool medium = false)
        {
            var l = Text(text, D(dp), color, medium);
            l.style.unityTextAlign = TextAnchor.MiddleLeft;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            return l;
        }

        /// <summary>Абзац макета: слева, с переносами.</summary>
        public static Label Para(Func<string> text, float dp, Color color, bool medium = false)
        {
            var l = Text(text, D(dp), color, medium);
            l.style.unityTextAlign = TextAnchor.UpperLeft;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        /// <summary>Шапка экрана списка: стрелка «назад» артом, заголовок и
        /// подзаголовок прописными — как на макете («ТЕКУЩИЕ ЭКСПЕДИЦИИ /
        /// ВЫБЕРИТЕ НУЖНУЮ ЭПОХУ»). Без облика стрелка — кнопкой темы.</summary>
        public static VisualElement Header(string skin, ILvnAssets assets,
                                           Func<string> title, Func<string> hint, Action onBack)
        {
            var row = ScreenUi.Row(new VisualElement { name = "stage-header" });
            row.style.flexShrink = 0;
            row.style.height = D(48f);
            row.style.alignItems = Align.Center;

            VisualElement back;
            if (!string.IsNullOrEmpty(skin))
            {
                back = new VisualElement();
                back.style.width = D(42f); back.style.height = D(48f);
                back.Add(Art(SkinUrl(skin, "back.png"), assets, 0f, 0f, D(42f), D(48f), bleed: 0f));
                back.AddManipulator(new Clickable(onBack));
                LvnMotion.Tappable(back);
            }
            else back = ScreenUi.BackButton(onBack, D(42f), D(24f));
            back.name = "stage-header-back";
            back.style.flexShrink = 0;
            row.Add(back);

            var words = new VisualElement { pickingMode = PickingMode.Ignore };
            words.style.marginLeft = D(16f);
            words.style.flexGrow = 1; words.style.flexShrink = 1;
            var head = Line(() => (title() ?? string.Empty).ToUpperInvariant(), 24f, Ink.Gold);
            head.name = "stage-header-title";
            words.Add(head);
            var sub = Line(() => (hint() ?? string.Empty).ToUpperInvariant(), 14f, Ink.Sand);
            sub.name = "stage-header-hint";
            sub.style.marginTop = D(4f);
            words.Add(sub);
            row.Add(words);
            return row;
        }

        // ── плашки ───────────────────────────────────────────────────────────

        /// <summary>ПЛАШКА-КНОПКА макета: шестигранная рамка артом, слово
        /// золотом по центру. Без <paramref name="onTap"/> — просто плашка
        /// (лента-заголовок), тап через неё проходит.</summary>
        public static VisualElement Plate(string skin, ILvnAssets assets, LvnStageSkin.Frame f, string file,
                                          Func<string> text, float dp, Action onTap, string name = null,
                                          Color? ink = null, bool medium = true)
        {
            var b = new VisualElement { name = name };
            b.style.width = D(f.Width); b.style.height = D(f.Height);
            b.style.flexShrink = 0;
            b.style.justifyContent = Justify.Center;
            b.style.alignItems = Align.Center;
            b.Add(Art(SkinUrl(skin, file), assets, 0f, 0f, D(f.Width), D(f.Height)));
            if (text != null)
            {
                var l = Text(() => (text() ?? string.Empty).ToUpperInvariant(), D(dp), ink ?? Ink.Gold, medium);
                l.style.paddingBottom = D(2f);   // нижняя грань плашки толще
                b.Add(l);
            }
            if (onTap != null)
            {
                b.AddManipulator(new Clickable(onTap));
                LvnMotion.Tappable(b);
            }
            else b.pickingMode = PickingMode.Ignore;
            return b;
        }

        /// <summary>Плашка с иконкой вместо слова (закладка, «заново», письмо).</summary>
        public static VisualElement IconPlate(string skin, ILvnAssets assets, LvnStageSkin.Frame f, string file,
                                              string icon, float iconDp, Action onTap, string name = null)
        {
            var b = Plate(skin, assets, f, file, null, 0f, onTap, name);
            b.Add(Icon(skin, assets, icon, iconDp));
            return b;
        }

        /// <summary>Иконка макета — квадрат в потоке, арт без запаса.</summary>
        public static VisualElement Icon(string skin, ILvnAssets assets, string file, float dp)
        {
            var w = new VisualElement { name = "stage-icon", pickingMode = PickingMode.Ignore };
            w.style.width = D(dp); w.style.height = D(dp);
            w.style.flexShrink = 0;
            w.Add(Art(SkinUrl(skin, file), assets, 0f, 0f, D(dp), D(dp), bleed: 0f));
            return w;
        }

        /// <summary>Плашка жанра или статуса: рамка в 1, скругление 4, слово в 12.</summary>
        public static VisualElement Chip(Func<string> text, Color edge, Color ink, Color bg)
        {
            var c = new VisualElement { name = "stage-chip", pickingMode = PickingMode.Ignore };
            c.style.flexShrink = 0;
            c.style.marginLeft = D(4f);
            LvnAir.Pad(c, D(8f), D(4f));
            c.style.backgroundColor = bg;
            LvnChrome.Frame(c, D(4f), edge, D(1f));
            c.Add(Text(text, D(12f), ink));
            return c;
        }

        /// <summary>Плашка жанра: цвет кромки — из <c>ui.browse.genre_colors</c>
        /// по имени жанра; слово светлее кромки, подложка — почти чёрная её
        /// тона (Детектив: #656565 → #b7b7b7 на #130a08, как в макете).
        /// Неназванный жанр носит плашку статуса.</summary>
        public static VisualElement GenreChip(string genre, Dictionary<string, string> colors)
        {
            // Авторский цвет — через словарь имён: так проходят и «#656565»,
            // и слово темы, а неизвестное молча остаётся кромкой витрины.
            Color edge = colors != null && genre != null && colors.TryGetValue(genre, out var hex)
                ? UiColor.Named(hex, Ink.Edge) : Ink.Edge;
            bool own = edge != Ink.Edge;
            return Chip(() => genre,
                        edge,
                        own ? UiColor.Lighter(edge, 0.45f) : Ink.Sky,
                        own ? Color.Lerp(edge, Color.black, 0.85f) : Ink.ChipBg);
        }

        /// <summary>Плашка статуса истории — тона витрины.</summary>
        public static VisualElement StatusChip(Func<string> text) => Chip(text, Ink.Edge, Ink.Sky, Ink.ChipBg);

        /// <summary>Разделитель макета: линия, две точки, ромб — артом на всю ширину.</summary>
        public static VisualElement Divider(string skin, ILvnAssets assets)
        {
            var d = new VisualElement { name = "stage-divider", pickingMode = PickingMode.Ignore };
            d.style.height = D(7f);
            d.style.flexShrink = 0;
            var art = Art(SkinUrl(skin, "divider.png"), assets, 0f, 0f, 0f, D(7f), bleed: 0f);
            art.style.width = StyleKeyword.Auto; art.style.left = 0; art.style.right = 0;
            d.Add(art);
            return d;
        }

        // ── ход новеллы ──────────────────────────────────────────────────────

        /// <summary>ХОД НОВЕЛЛЫ на карточке и в окне детали: полоса 87×9 и
        /// «Глава 5/12» (на карточке — полоса выше слова, в детали — ниже);
        /// пройденная новелла вместо них показывает галочку и «Пройдено».</summary>
        public static VisualElement Course(LvnTitle t, string skin, ILvnAssets assets, bool textFirst)
        {
            var box = new VisualElement { name = "stage-course", pickingMode = PickingMode.Ignore };
            box.style.flexShrink = 0;
            if (LvnProgress.Finished(t))
            {
                var row = ScreenUi.Row(new VisualElement { pickingMode = PickingMode.Ignore });
                row.name = "stage-course-done";
                row.style.alignItems = Align.Center;
                row.Add(Icon(skin, assets, "icon-check.png", 12f));
                var word = Line(() => LvnWords.Of("hub.done", "Completed"), 12f, Ink.Gold);
                word.style.marginLeft = D(4f);
                row.Add(word);
                box.Add(row);
                return box;
            }
            var bar = Progress(out var fill);
            bar.name = "stage-course-bar";
            bar.style.width = D(87f);
            LvnStageKit.Fill(fill, LvnProgress.Fraction(t));
            var counter = Line(() => BrowseHub.ChapterCounter(t), 12f, Ink.Sky);
            counter.name = "stage-course-counter";
            if (textFirst) { box.Add(counter); bar.style.marginTop = D(4f); box.Add(bar); }
            else { box.Add(bar); counter.style.marginTop = D(4f); box.Add(counter); }
            return box;
        }
    }
}
