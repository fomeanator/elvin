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
            _chart.Live = on;
            if (on) { _deviceRows = null; _deviceChapters = null; }   // на разворот — свежий взгляд на диск
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
            PlaceCapsule(k);
        }

        // ── место капсулы: строка бара или циферблат логотипа ────────────────

        /// <summary>Кольцо на часах — чуть шире циферблата, тонкое.</summary>
        private const float DialRingScale = 1.35f, DialRingStroke = 3f;
        private bool _onDial;

        /// <summary>Циферблат, на который можно сесть: дан оболочкой, размерен и
        /// не в главе — там кружок баблик у левого края сцены, шапки нет.</summary>
        private Rect? DialOnScreen()
        {
            if (Lvn.UI.LvnScreenDirector.Current.InChapter) return null;
            var r = MiniAnchor?.Invoke();
            if (r == null || float.IsNaN(r.Value.width) || r.Value.width <= 1f) return null;
            return r;
        }

        /// <summary>ПОСТАВИТЬ КАПСУЛУ. Свёрнутая — на циферблат логотипа, если
        /// он на экране; лист — в строке бара по центру, как всегда; между ними
        /// по ходу морфа, чтобы лист вырастал из кольца, а не прыгал к строке.
        /// Без циферблата капсула живёт в потоке строки бара, как прежде.</summary>
        private void PlaceCapsule(float k)
        {
            var dial = DialOnScreen();
            if (dial == null)
            {
                if (_onDial) LeaveDial();
                return;
            }
            float rootW = resolvedStyle.width;
            if (float.IsNaN(rootW) || rootW <= 1f) return;   // до первой раскладки
            var d = dial.Value;
            float w = Mathf.Lerp(MiniSize, _fullW, k);
            float barTop = _sheetTop > 0f ? _sheetTop : _safeTop + 5f;
            _capsule.style.position = Position.Absolute;
            _capsule.style.marginTop = 0f;
            _capsule.style.marginLeft = 0f;
            _capsule.style.left = Mathf.Lerp(d.center.x - MiniSize * 0.5f, (rootW - w) * 0.5f, k);
            _capsule.style.top = Mathf.Lerp(d.center.y - MiniSize * 0.5f, barTop, k);
            // На часах капсула — только кольцо: тон и кромка накрыли бы
            // циферблат, ради которого кружок сюда и сел. Зона нажатия при
            // этом остаётся прежней — MiniSize вокруг центра часов.
            _capsule.style.backgroundColor = UiColor.WithAlpha(LvnTokens.PanelBg,
                Mathf.Lerp(0f, StageDressed ? 0f : 1f, k));
            if (k < 0.05f) LvnChrome.ClearBorder(_capsule);
            if (!_onDial)
            {
                _onDial = true;
                float ring = d.width * DialRingScale;
                _miniRing.style.width = ring; _miniRing.style.height = ring;
                _miniRing.SetGeometry(ring * 0.5f - DialRingStroke * 0.5f - 1f, DialRingStroke, drawArrow: false);
            }
        }

        /// <summary>Циферблата больше нет (глава, обычная шапка) — капсула
        /// возвращается в поток строки бара со своим баблик-кольцом.</summary>
        private void LeaveDial()
        {
            _onDial = false;
            _capsule.style.position = Position.Relative;
            _capsule.style.left = StyleKeyword.Null;
            _capsule.style.top = StyleKeyword.Null;
            LvnChrome.Edge(_capsule);
            _miniRing.style.width = MiniSize; _miniRing.style.height = MiniSize;
            _miniRing.SetGeometry(MiniSize * 0.5f - 5f, 3.5f, drawArrow: true);
            ApplyMorph(_morph);   // отступ строки и тон — как в потоке
        }
    }
}
