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

        internal void SetExpanded(bool on)
        {
            if (_expanded == on) return;
            _expanded = on;
            _scrim.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            // Секции собираются ПОСЛЕ старта морфа (офлайн-ветка проверяет
            // кэш на диске — десятки миллисекунд, и они не должны съедать
            // первые кадры разворота). Вся карточка раскрывается одним жестом.
            if (on)
            {
                // ЛИСТ РАСТЁТ ИЗ КРУЖКА НА МЕСТЕ («я бы сверху оставил, он же
                // перетекает с анимацией из крутилки» — Илья 10.09): кружок
                // живёт в шапке, и лист у низа заставлял капсулу перелетать
                // через весь экран. Половина экрана вниз от кружка, но не ниже
                // домашней полосы телефона. Ширина — экран без полей; с обликом
                // поля по паспорту листа.
                float avail = resolvedStyle.height;
                float bottomInset = Lvn.UI.LvnEdges.Insets(this).y;
                _sheetTop = _safeTop + 5f;
                float ceiling = avail - _sheetTop - bottomInset - 24f;
                _fullH = avail > 100f
                    ? Mathf.Clamp(avail * SheetHeightShare, MiniSize, Mathf.Max(MiniSize, ceiling))
                    : 560f;
                float availW = resolvedStyle.width;
                float side = StageDressed ? D(LvnStageSkin.Sheet.Side) : SheetSide;
                _fullW = availW > 100f ? availW - side * 2f : 520f;
                // Новый разворот — с начала списка: место прокрутки хранится
                // между пересборками (LvnScroll.Keeping), но не между визитами.
                if (_sections != null) _sections.scrollOffset = Vector2.zero;
                _capsule.schedule.Execute(() => { if (_expanded) RebuildSections(); }).ExecuteLater(70);
            }
            float from = _morph, to = on ? 1f : 0f;
            _capsule.experimental.animation.Start(0f, 1f, LvnMotion.Ms(340), (_, p) =>
            {
                float e = LvnMotion.Settle(p);
                ApplyMorph(Mathf.Lerp(from, to, e));
            });
        }

        private void ApplyMorph(float k)
        {
            _morph = k;
            ApplyChapterMode();
            _capsule.style.width = Mathf.Lerp(MiniSize, _fullW, k);
            _capsule.style.height = Mathf.Lerp(MiniSize, _fullH, k);
            // Кружок сидит под вырезом в строке бара; лист растёт с того же места.
            _capsule.style.marginTop = _sheetTop > 0f ? _sheetTop : _safeTop + 5f;
            // С обликом угол мал: капсула режет содержимое, и большой радиус
            // срезал бы угловые скобы рамки-арта.
            LvnChrome.Round(_capsule, Mathf.Lerp(MiniSize * 0.5f, StageDressed ? D(4f) : 22f, k));
            // Кружок — полупрозрачный тон (Илья, 26.08), а ЛИСТ — глухой:
            // на пол-экрана цифр сквозь 6 % просвета проступала витрина
            // («ТЕКУЩИЕ ЭКСПЕДИЦИИ», героиня), и скорость читалась поверх
            // призрака. С обликом заливка уходит: фон рисует рамка-арт листа.
            // За листом встаёт затемнение, как у попапов оболочки: витрина
            // гаснет, внимание — на загрузках.
            _capsule.style.backgroundColor = UiColor.WithAlpha(LvnTokens.PanelBg,
                Mathf.Lerp(0.94f, StageDressed ? 0f : 1f, k));
            _scrim.style.backgroundColor = UiColor.WithAlpha(LvnTokens.Scrim, LvnTokens.Scrim.a * k);
            // Верхняя кромка наливается акцентом по мере разворота — та же
            // «крышка», что у попап-экранов оболочки (AdoptSheet). С обликом
            // кромка гаснет: у рамки-арта своя.
            LvnChrome.EdgeOn(_capsule, LvnSide.Top,
                Color.Lerp(LvnTokens.Border, LvnTokens.Accent, k), Mathf.Lerp(1f, StageDressed ? 0f : 2.5f, k));
            // Кроссфейд содержимого: мини-кольцо гаснет в первой трети морфа,
            // полная карточка проявляется во второй — в середине капсула
            // «пустая», и перетекание читается формой, а не мешаниной слоёв.
            _miniRing.style.opacity = Mathf.Clamp01(1f - k * 3f);
            _full.style.opacity = Mathf.Clamp01((k - 0.65f) / 0.35f);
            _full.style.visibility = k > 0.65f ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
