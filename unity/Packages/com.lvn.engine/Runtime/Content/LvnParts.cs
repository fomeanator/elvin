using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>Один файл контента: адрес, вид (для правил загрузки) и
    /// объявленный размер (0 — автор не назвал).</summary>
    public readonly struct LvnPart
    {
        public readonly string Url;
        public readonly string Kind;
        public readonly long Size;

        /// <summary>ОБЯЗАТЕЛЬНАЯ часть: без неё главу не начать. Скрипт и фон
        /// загрузки — всегда; ассеты — как объявил автор (<c>critical</c>).
        /// Остальное подождёт своей сцены и качается по ходу.</summary>
        public readonly bool Critical;

        public LvnPart(string url, string kind, long size = 0, bool critical = false)
        {
            Url = url; Kind = kind; Size = size; Critical = critical;
        }
    }

    /// <summary>
    /// ИЗ ЧЕГО СОСТОИТ КОНТЕНТ — единственный перечень файлов новеллы, главы и
    /// оболочки.
    ///
    /// <para>Знание «глава = скрипт + фон + объявленные ассеты» было записано
    /// ШЕСТЬ РАЗ: греем всё, планируем скачивание по главам, ставим главу в
    /// очередь, считаем «глава целиком на диске», убираем диск и оцениваем
    /// «докачать текущую». Шесть перечислений, шесть глаголов — и одно
    /// добавленное поле главы означало бы пять мест, которые о нём не узнают.
    /// Расхождение уже было: арт карточки хаба один обход брал как
    /// <c>card.image ?? cover_url</c>, а соседний — только <c>card.image</c>, и
    /// новелла без своей карточки выпадала из набора «не выгружать».</para>
    ///
    /// <para>Здесь — только ЧТО перечислять. Что с этим делать (греть, качать,
    /// проверять кэш, беречь от уборки) остаётся у вызывающего: у каждого свой
    /// глагол, но список один.</para>
    /// </summary>
    public static class LvnParts
    {
        public const string Sprite = "sprite";
        public const string Script = "script";
        public const string Audio = "audio";

        /// <summary>Файлы ОДНОЙ ГЛАВЫ.</summary>
        public static IEnumerable<LvnPart> OfChapter(LvnChapter ch)
        {
            if (ch == null) yield break;
            if (!string.IsNullOrEmpty(ch.script_url))
                yield return new LvnPart(ch.script_url, Script, 0, critical: true);
            if (!string.IsNullOrEmpty(ch.bg_url))
                yield return new LvnPart(ch.bg_url, Sprite, 0, critical: true);
            if (ch.assets == null) yield break;
            foreach (var kv in ch.assets)
                if (!string.IsNullOrEmpty(kv.Key))
                    yield return new LvnPart(kv.Key, kv.Value?.kind ?? Sprite,
                                             kv.Value?.size ?? 0, kv.Value?.critical ?? false);
        }

        /// <summary>Арт САМОЙ НОВЕЛЛЫ. Обложка и арт карточки — РАЗНЫЕ файлы,
        /// когда автор задал <c>card.image</c>: карусель рисует одно, хаб
        /// другое. Грели только обложку — карточка хаба ждала сеть уже после
        /// «всё скачано».</summary>
        public static IEnumerable<LvnPart> OfTitleArt(LvnTitle t)
        {
            if (t == null) yield break;
            if (!string.IsNullOrEmpty(t.cover_url)) yield return new LvnPart(t.cover_url, Sprite);
            var card = t.CardArt();
            if (!string.IsNullOrEmpty(card) && card != t.cover_url) yield return new LvnPart(card, Sprite);
        }

        /// <summary>Новелла целиком: её арт и все её главы.</summary>
        public static IEnumerable<LvnPart> OfTitle(LvnTitle t)
        {
            foreach (var p in OfTitleArt(t)) yield return p;
            if (t == null) yield break;
            foreach (var ch in t.ChaptersOf())
                foreach (var p in OfChapter(ch))
                    yield return p;
        }

        /// <summary>Картинки, которые рисует МЕНЮ: обложки, арт карточек новелл
        /// и коллекций, фоны глав (их показывает экран загрузки). Уборка после
        /// главы не вправе их выгружать — витрина рисует их прямо сейчас.</summary>
        public static IEnumerable<LvnPart> OfMenuArt(LvnManifest m)
        {
            if (m?.titles != null)
                foreach (var t in m.titles)
                {
                    if (t == null) continue;
                    foreach (var p in OfTitleArt(t)) yield return p;
                    foreach (var ch in t.ChaptersOf())
                        if (ch != null && !string.IsNullOrEmpty(ch.bg_url))
                            yield return new LvnPart(ch.bg_url, Sprite);
                }
            if (m?.collections == null) yield break;
            foreach (var col in m.collections)
                if (!string.IsNullOrEmpty(col?.card?.image))
                    yield return new LvnPart(col.card.image, Sprite);
        }

        /// <summary>Звучание ОБОЛОЧКИ: музыка витрины и звуки интерфейса.</summary>
        public static IEnumerable<LvnPart> OfShellSound(LvnManifest m)
        {
            var ui = m?.ui;
            if (ui == null) yield break;
            foreach (var url in new[] { ui.browse?.music, ui.sounds?.click, ui.sounds?.choice, ui.sounds?.type })
                if (!string.IsNullOrEmpty(url))
                    yield return new LvnPart(url, Audio);
        }

        /// <summary>Весь контент манифеста. Повторы возможны (обложка новеллы —
        /// и её часть, и картинка меню) и безвредны: их отсеет набор адресов у
        /// того, кто считает.</summary>
        public static IEnumerable<LvnPart> OfAll(LvnManifest m)
        {
            if (m?.titles != null)
                foreach (var t in m.titles)
                    foreach (var p in OfTitle(t))
                        yield return p;
            foreach (var p in OfMenuArt(m)) yield return p;
            foreach (var p in OfShellSound(m)) yield return p;
        }
    }
}
