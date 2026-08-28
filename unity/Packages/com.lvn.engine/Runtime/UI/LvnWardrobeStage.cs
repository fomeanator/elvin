using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>Что за вещь живёт на этой оси гардероба. Имя оси придумывает
    /// автор новеллы («hairstyle», «причёска», «armor»), поэтому смысл
    /// угадывается по названию — но угадывается В ОДНОМ МЕСТЕ.</summary>
    public enum LvnWardrobeAxisKind
    {
        /// <summary>Причёска или цвет волос — вещь на голове.</summary>
        Hair,
        /// <summary>Украшение: серьги, ожерелье, венок. Витрина показывает их
        /// кроп-иконками, а не кадром фигуры.</summary>
        Decor,
        /// <summary>Эмоция: лицо, настроение. Не вещь вовсе — эту ось лист
        /// показывает отдельной колонкой, а сцена не считает переодеванием.</summary>
        Emotion,
        /// <summary>Одежда: платье, броня, форма — вещь на корпусе.</summary>
        Outfit,
    }

    /// <summary>
    /// ВИТРИНА ГАРДЕРОБА: чем ось является и как её показывать.
    ///
    /// <para>Правило «ось про волосы?» было списано в двух пакетах слово в
    /// слово — сцена решала, откуда лить кроссфейд, лист решал, какую рисовать
    /// иконку, и любая правка требовала помнить про обе копии. Кадр витрины
    /// (насколько приблизить фигуру и на какую её часть навести) жил третьим
    /// набором чисел внутри метода экрана, хотя это знание о персонаже: у
    /// одного арта голова в верхней трети, у другого — в четверти.</para>
    ///
    /// <para>Движковые значения рассчитаны на обычную портретную куклу и
    /// покрывают почти всё; новелла перекрывает нужную ось через
    /// <c>ui.wardrobe.framing</c>, а поменять их можно и на лету.</para>
    /// </summary>
    public static class LvnWardrobeStage
    {
        // ── смысл оси ─────────────────────────────────────────────────────────

        public static LvnWardrobeAxisKind KindOf(string axis)
        {
            // «ё» пишут и не пишут — обе записи одного слова. Прежнее правило
            // (в двух копиях) знало только «причес», поэтому ось с именем
            // «Причёска» проходила как одежда и получала кадр на корпус.
            var key = (axis ?? "").ToLowerInvariant().Replace('ё', 'е');
            // Эмоция первой: она не вещь, и спутать её с одеждой дороже всего —
            // сцена приняла бы смену лица за переодевание. Правило стояло ДВАЖДЫ
            // слово в слово (тракт показа актёра и лента листа).
            if (key.Contains("emo") || key.Contains("эмо") || key == "mood" || key == "face")
                return LvnWardrobeAxisKind.Emotion;
            if (key.Contains("hair") || key.Contains("причес") || key.Contains("волос"))
                return LvnWardrobeAxisKind.Hair;
            if (key.Contains("decor") || key.Contains("jewel") || key.Contains("acc")
                || key.Contains("украш")) return LvnWardrobeAxisKind.Decor;
            return LvnWardrobeAxisKind.Outfit;
        }

        /// <summary>Вещь на голове? Сцена по этому признаку льёт смену облика
        /// сверху вниз, лист — рисует корону вместо вешалки.</summary>
        public static bool IsHair(string axis) => KindOf(axis) == LvnWardrobeAxisKind.Hair;

        /// <summary>Ось лица, а не гардероба. Спрашивают двое: тракт показа
        /// актёра (смена эмоции — не переодевание) и лента листа (эмоции живут
        /// отдельной колонкой).</summary>
        public static bool IsEmotion(string axis) => KindOf(axis) == LvnWardrobeAxisKind.Emotion;

        /// <summary>Значок раздела, когда новелла не дала своего (slot.icon).</summary>
        public static LvnIcon IconFor(string axis)
            => KindOf(axis) == LvnWardrobeAxisKind.Hair ? LvnIcon.Crown : LvnIcon.Wardrobe;

        // ── кадр витрины ──────────────────────────────────────────────────────
        // zoom — во сколько раз фигура крупнее плитки, anchorY — какая её высота
        // окажется в середине плитки (0 — макушка, 1 — ступни).

        /// <summary>Причёска: голова крупно, чуть выше середины кадра.</summary>
        public static float HairZoom = 1.60f, HairAnchorY = 0.35f;
        /// <summary>Одежда: корпус, кадр ниже середины.</summary>
        public static float OutfitZoom = 1.55f, OutfitAnchorY = 0.60f;
        /// <summary>Украшения приходят готовыми кроп-иконками (вырезаны по
        /// содержимому при импорте) — приближать нечего.</summary>
        public static float DecorZoom = 1f, DecorAnchorY = 0.5f;
        /// <summary>Сборная вкладка «Моё»: фигура целиком, лёгкий наезд.</summary>
        public static float AllZoom = 1.07f, AllAnchorY = 0.5f;

        /// <summary>Ось, показывающая всё сразу («Моё»). Строка живёт здесь,
        /// потому что кадр для неё выбирается тоже здесь.</summary>
        public const string AllAxis = "__all__";

        private static readonly Dictionary<string, (float zoom, float anchorY)> _custom
            = new Dictionary<string, (float, float)>();

        /// <summary>Кадр витрины для оси: во сколько раз приблизить фигуру и на
        /// какую её часть навестись.</summary>
        public static (float zoom, float anchorY) Framing(string axis)
        {
            if (axis == AllAxis) return (AllZoom, AllAnchorY);
            if (!string.IsNullOrEmpty(axis) && _custom.TryGetValue(axis, out var own)) return own;
            switch (KindOf(axis))
            {
                case LvnWardrobeAxisKind.Hair: return (HairZoom, HairAnchorY);
                case LvnWardrobeAxisKind.Decor: return (DecorZoom, DecorAnchorY);
                default: return (OutfitZoom, OutfitAnchorY);
            }
        }

        // ── компоновка листа ──────────────────────────────────────────────────

        /// <summary>Колонка эмоций начинается на этой доле свободного зазора
        /// под верхней строкой — чтобы не липнуть к ней вплотную.</summary>
        public static float EmotionsTopFraction = 0.10f;

        /// <summary>И занимает эту долю зазора: на всю высоту колонка закрывала
        /// бы куклу, остальные лица доступны прокруткой.</summary>
        public static float EmotionsHeightFraction = 0.575f;

        /// <summary>Применить настройки новеллы (<c>ui.wardrobe.framing</c>):
        /// ось → кадр. Пусто — остаются движковые значения.</summary>
        public static void Apply(WardrobeConfig cfg)
        {
            _custom.Clear();
            if (cfg?.framing == null) return;
            foreach (var kv in cfg.framing)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                var (z, y) = Framing(kv.Key);
                _custom[kv.Key] = (
                    kv.Value.zoom.HasValue ? Mathf.Clamp(kv.Value.zoom.Value, 0.2f, 6f) : z,
                    kv.Value.y.HasValue ? Mathf.Clamp01(kv.Value.y.Value) : y);
            }
        }
    }
}
