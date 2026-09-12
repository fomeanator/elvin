using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// РИТМ ЛЕНТЫ — одни числа на все полки.
    ///
    /// <para>Отступы и размеры плиток подбирались по месту: у полосы своя
    /// маржа, у шапки своя, у плитки свой правый зазор — и три полки на одном
    /// экране дышали по-разному. Здесь ритм назван один раз: размер плитки,
    /// три зазора, высота пустой полки, доли свечения и вуали. Полка берёт
    /// числа отсюда, тест сверяет каждую полку с ними.</para>
    ///
    /// <para>Размер плитки — не токен, а подобранное с Ильёй число (500 → 460
    /// → 391 → 405 → 429 на живом экране со спайн-постерами); зазоры — шкала
    /// токенов, чтобы полки жили в том же ритме, что и остальная оболочка.</para>
    /// </summary>
    public sealed partial class BrowseHub
    {
        // ── имена: по ним ходят тесты и тур ──────────────────────────────────
        internal const string ShelfName = "hub-shelf";
        internal const string ShelfHeadName = "hub-shelf-head";
        internal const string ShelfCountName = "hub-shelf-count";
        internal const string ShelfEmptyName = "hub-shelf-empty";
        internal const string ShelfCardName = "hub-shelf-card";
        internal const string ShelfLockName = "hub-shelf-lock";
        internal const string ShelfGlowName = "hub-shelf-glow";

        // ── плитка ───────────────────────────────────────────────────────────
        internal const float CardW = 429f;     // подобрано с Ильёй: 500 → 460 → 391 → 405 → 429
        internal const float PosterH = 564f;   // пропорция спайна (w/h 0.8315) + запас высоты
        internal const float CaptionH = 112f;  // цоколь с названием и метаданными

        /// <summary>Рост плитки и полосы — одним числом: постер плюс цоколь.
        /// Полоса, посчитанная отдельно, однажды обрезала плитки снизу.</summary>
        internal static float ShelfCardHeight => PosterH + CaptionH;

        // ── зазоры ───────────────────────────────────────────────────────────
        internal static float CardGap => LvnTokens.Space3;   // между плитками
        internal static float ShelfGap => LvnTokens.Space5;  // между полками
        internal static float HeadGap => LvnTokens.Space2;   // под шапкой полки

        /// <summary>Пустая полка — сообщение о разделе, а не плитка-призрак:
        /// тот же горизонтальный ритм, но не высота целого постера.</summary>
        internal static float EmptyShelfHeight => LvnTokens.Space4 * 2f + LvnTokens.TouchLg;

        // ── вес плитки ───────────────────────────────────────────────────────
        internal const float GlowPercent = 52f;   // свечение под активной — нижняя половина постера
        internal const float GlowAlpha = 0.26f;   // сдержанное: акцент остаётся у кнопок
        internal const float LockedVeil = 0.45f;  // замок гасит обложку, подпись читается

        /// <summary>Сколько плиток видно на ширине хаба: по ним идёт волна
        /// проявления, остальные за кромкой ждать её незачем.</summary>
        internal static int VisibleCardsAt(float hubWidth)
            => Mathf.Max(1, Mathf.CeilToInt((hubWidth - LvnEdges.PageSide * 2f) / (CardW + CardGap)));
    }
}
