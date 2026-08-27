using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ОДИН ПРЕДМЕТ В ДВУХ ВИДАХ — кружок в углу и раскрытая панель.
    ///
    /// <para>Это не два экрана, между которыми переключаются, а одна вещь,
    /// которая раскрывается: кружок растёт в панель и сжимается обратно тем же
    /// движением. Поэтому здесь нет «показать/скрыть» — есть одно число
    /// раскрытия, из которого считаются размеры, скругления и прозрачности.
    /// Раздельные состояния разъезжались на каждой правке: панель уже открыта,
    /// а кружок ещё не спрятан.</para>
    /// </summary>
    public sealed partial class DownloadHud
    {
        // ── морф мини ↔ полная ────────────────────────────────────────────────

        private void SetExpanded(bool on)
        {
            if (_expanded == on) return;
            _expanded = on;
            _scrim.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            // Секции собираются ПОСЛЕ старта морфа (офлайн-ветка проверяет
            // кэш на диске — десятки миллисекунд, и они не должны съедать
            // первые кадры разворота). Каскад — только при развороте.
            if (on)
            {
                float avail = resolvedStyle.height;
                _fullH = avail > 100f
                    ? Mathf.Clamp(avail - 112f - 24f, 300f, FullHMax)
                    : FullHMax;
                float availW = resolvedStyle.width;
                _fullW = availW > 100f
                    ? Mathf.Clamp(availW * 0.6f, 420f, FullWMax)
                    : 520f;
                _capsule.schedule.Execute(() => RebuildSections(animate: true)).ExecuteLater(70);
            }
            float from = _morph, to = on ? 1f : 0f;
            _capsule.experimental.animation.Start(0f, 1f, 260, (_, p) =>
            {
                float e = 1f - Mathf.Pow(1f - p, 3f); // OutCubic — тормозит у цели
                ApplyMorph(Mathf.Lerp(from, to, e));
            });
        }

        private void ApplyMorph(float k)
        {
            _morph = k;
            _capsule.style.width = Mathf.Lerp(MiniSize, _fullW, k);
            _capsule.style.height = Mathf.Lerp(MiniSize, _fullH, k);
            LvnChrome.Round(_capsule, Mathf.Lerp(MiniSize * 0.5f, 22f, k));
            // Верхняя кромка наливается акцентом по мере разворота — та же
            // «крышка», что у попап-экранов оболочки (AdoptSheet).
            _capsule.style.borderTopWidth = Mathf.Lerp(1f, 2.5f, k);
            _capsule.style.borderTopColor = Color.Lerp(LvnTokens.Border, LvnTokens.Accent, k);
            // Кроссфейд содержимого: мини-кольцо гаснет в первой трети морфа,
            // полная карточка проявляется во второй — в середине капсула
            // «пустая», и перетекание читается формой, а не мешаниной слоёв.
            _miniRing.style.opacity = Mathf.Clamp01(1f - k * 3f);
            _full.style.opacity = Mathf.Clamp01((k - 0.55f) / 0.45f);
        }
    }
}
