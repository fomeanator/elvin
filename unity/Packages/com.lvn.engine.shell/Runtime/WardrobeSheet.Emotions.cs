using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЛИЦА ГЕРОИНИ — колонка справа: сборка, место, пересборка, вид и полоса
    /// прокрутки.
    ///
    /// <para>Тема была размазана по двум файлам: сборку держал конструктор
    /// листа, а поведение — файл ЛЕНТЫ СКИНОВ, к лицам отношения не имеющий.
    /// Разошлись бы правила — и никто бы не связал: «колонка наезжает на
    /// навбар» правится в одном файле, «кружки не двигаются» в другом.</para>
    ///
    /// <para>Правила темы, каждое из живого репорта: эмоции НЕ коммитятся в
    /// слоты («Выбрать» их не сохраняет, закрытие возвращает лицо по
    /// умолчанию); обычного скроллбара нет, потому что горизонтальный ряд
    /// «странно скроллился», и кружки сбоку — единственный ответ на «сколько
    /// там ещё»; верх колонки прибит к низу навбара, иначе она наезжает на
    /// него, когда лиц много.</para>
    /// </summary>
    public sealed partial class WardrobeSheet
    {
        /// <summary>
        /// КОЛОНКА ЛИЦ СПРАВА ОТ ГЕРОИНИ — вместе с индикатором прокрутки.
        ///
        /// <para>Идея Ильи 27.08 («уникальная штука»): эмоции живут не рядом с
        /// нарядами, а отдельной колонкой над листом — тем же приёмом, что и
        /// пилюли кошелька слева. Тап примеряет лицо на живую куклу, но в
        /// гардеробные слоты ось не входит: «Выбрать» её не коммитит, закрытие
        /// возвращает лицо по умолчанию.</para>
        ///
        /// <para>Индикатор прокрутки — часть той же работы, а не украшение: в
        /// колонке лиц больше, чем помещается, а обычного скроллбара тут нет
        /// (горизонтальный ряд «странно скроллился» — живой репорт). Кружки
        /// сбоку и есть единственный ответ на «сколько там ещё».</para>
        ///
        /// <para>И правило места: верх колонки прибит к низу навбара, высота
        /// ограничена зазором до плашки — иначе колонка, растущая вверх,
        /// наезжала на навбар, когда лиц много (Илья 28.08: «баблы
        /// перекрываются»).</para>
        /// </summary>
        private void BuildEmotionColumn()
        {
            // БАБЛИКИ ЭМОЦИЙ (идея Ильи 27.08 — «уникальная штука»): колонка
            // лиц СПРАВА ОТ ГЕРОИНИ, над листом (как пилюли кошелька слева —
            // тот же приём bottom:100%). Тап примеряет эмоцию на живую куклу
            // через Preview оси `emotion`. В гардеробные слоты ось не входит —
            // «Выбрать» её не коммитит, закрытие листа возвращает лицо по
            // умолчанию. Горизонтальный ряд в листе «странно скроллился»
            // (живой репорт) — вертикаль у правого края читается сама.
            _emotions = Lvn.UI.LvnScroll.Vertical();
            _emotions.style.position = Position.Absolute;
            _emotions.style.right = EmoBarLane; // полоса у самого края — под индикатор
            _emotions.style.display = DisplayStyle.None;
            _emotions.contentContainer.style.alignItems = Align.FlexEnd;
            Add(_emotions);

            // ГДЕ МЫ В СПИСКЕ ЛИЦ (Илья 26.08: «показывать кружками скролл —
            // полупрозрачными прямоугольниками модными, справа место есть»):
            // сегментированная дорожка у правого края, по ней плавно скользит
            // бегунок. Своя, а не штатный скроллбар: колонка живёт поверх
            // куклы, и серая полоса Unity выбивалась бы из оболочки.
            _emoBar = new VisualElement { pickingMode = PickingMode.Ignore };
            _emoBar.style.position = Position.Absolute;
            _emoBar.style.right = 4;
            _emoBar.style.width = EmoBarWidth;
            _emoBar.style.display = DisplayStyle.None;
            Add(_emoBar);
            for (int s = 0; s < EmoBarSegments; s++)
            {
                var seg = new VisualElement { pickingMode = PickingMode.Ignore };
                seg.style.flexGrow = 1;
                seg.style.marginBottom = s == EmoBarSegments - 1 ? 0 : 4;
                seg.style.backgroundColor = new Color(1f, 1f, 1f, 0.13f);
                LvnChrome.Round(seg, EmoBarWidth * 0.5f);
                _emoBar.Add(seg);
            }
            _emoThumb = new VisualElement { pickingMode = PickingMode.Ignore };
            _emoThumb.style.position = Position.Absolute;
            _emoThumb.style.left = 0; _emoThumb.style.right = 0;
            _emoThumb.style.backgroundColor = new Color(1f, 1f, 1f, 0.62f);
            LvnChrome.Round(_emoThumb, EmoBarWidth * 0.5f);
            Smooth(_emoThumb, LvnMotion.Quick, "top", "height");
            _emoBar.Add(_emoThumb);
            // Скроллеры спрятаны, но живут — их значение и есть позиция.
            _emotions.verticalScroller.valueChanged += _ => UpdateEmoScrollBar();
            _emotions.RegisterCallback<GeometryChangedEvent>(_ => UpdateEmoScrollBar());
            // ПОД НАВБАРОМ (Илья 28.08: «баблы перекрываются — по топу, под
            // навбаром лучше»): колонка, растущая от плашки вверх, наезжала на
            // неё, когда лиц больше, чем зазора. Теперь верх колонки прибит к
            // низу навбара, а высота ограничена зазором до плашки — лишнее
            // скроллится внутри, перекрытий не бывает по построению.
            RegisterCallback<GeometryChangedEvent>(_ => PlaceEmotions());
        }

        // Колонка эмоций стоит от низа НАВБАРА до верха плашки — в координатах
        // листа, потому пересчёт на каждый layout: лист живёт на разной высоте
        // в меню и в игре, а safe area у каждого устройства своя.
        private void PlaceEmotions()
        {
            if (_emotions == null || panel == null) return;
            float sheetTop = worldBound.yMin;
            if (float.IsNaN(sheetTop) || sheetTop <= 0f) return;
            float safeTop = ScreenUi.SafeTop(this);
            float navBottom = safeTop + LvnTopBar.RowH + 10f;
            float gap = Mathf.Max(0f, sheetTop - navBottom - 12f);
            // Отступ от навбара — десятая доля зазора (Илья 26.08: «чуть ниже
            // на 10 процентов»), высота — та же половина зазора плюс 15%.
            float top = navBottom + gap * LvnWardrobeStage.EmotionsTopFraction;
            float height = Mathf.Max(120f, gap * LvnWardrobeStage.EmotionsHeightFraction);
            _emotions.style.top = top - sheetTop;
            _emotions.style.bottom = StyleKeyword.Auto;
            // ПОЛОВИНА зазора (Илья 28.08: «слишком много — сократи в 2 раза»):
            // колонка на всю высоту закрывала куклу; остальные лица скроллятся.
            _emotions.style.maxHeight = height;
            if (_emoBar != null)
            {
                _emoBar.style.top = top - sheetTop;
                _emoBar.style.height = height;
            }
            // Герои — та же полка у левого края: две колонки читаются как пара.
            if (_rosterRow != null)
            {
                _rosterRow.style.top = top - sheetTop;
                _rosterRow.style.maxHeight = height;
            }
            UpdateEmoScrollBar();
        }

        // Бегунок дорожки: длина — доля видимого списка, положение — доля
        // прокрутки. Дорожка прячется целиком, когда лица помещаются разом:
        // индикатор, который нечего индицировать, — просто шум.
        private void UpdateEmoScrollBar()
        {
            if (_emoBar == null || _emoThumb == null || _emotions == null) return;
            float view = _emotions.contentViewport.layout.height;
            float content = _emotions.contentContainer.layout.height;
            bool visible = _emotions.style.display != DisplayStyle.None;
            if (!visible || float.IsNaN(view) || float.IsNaN(content) || content <= view + 1f)
            {
                _emoBar.style.display = DisplayStyle.None;
                return;
            }
            _emoBar.style.display = DisplayStyle.Flex;
            float barH = _emoBar.layout.height;
            if (float.IsNaN(barH) || barH <= 1f) return;
            float thumbH = Mathf.Clamp(barH * (view / content), 26f, barH);
            float p = Mathf.Clamp01(_emotions.scrollOffset.y / Mathf.Max(1f, content - view));
            _emoThumb.style.height = thumbH;
            _emoThumb.style.top = (barH - thumbH) * p;
        }

        // ── баблики эмоций: примерка лица на живую куклу ─────────────────────
        private void RebuildEmotions()
        {
            if (_emotions == null) return;
            _emotions.Clear();
            _emotionAxis = null;
            List<string> vals = null;
            if (_def?.axes != null)
                foreach (var kv in _def.axes)
                {
                    // Ось лица опознаёт витрина (LvnWardrobeStage.IsEmotion) —
                    // здесь была вторая копия того же правила.
                    if (Lvn.UI.LvnWardrobeStage.IsEmotion(kv.Key)
                        && kv.Value != null && kv.Value.Count > 1)
                    { _emotionAxis = kv.Key; vals = kv.Value; break; }
                }
            // Ось, оформленная гардеробным слотом, — наряд, а не лицо.
            if (_emotionAxis != null && _def.wardrobe != null
                && _def.wardrobe.ContainsKey(_emotionAxis)) _emotionAxis = null;
            _emotions.style.display = _emotionAxis == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (_emotionAxis == null)
            {
                if (_emoBar != null) _emoBar.style.display = DisplayStyle.None;
                return;
            }

            foreach (var v in vals)
            {
                if (string.IsNullOrEmpty(v)) continue;
                var value = v;
                var chip = new Button(() =>
                {
                    LvnWardrobe.Preview(_entity, _emotionAxis, value); // лицо — живьём
                    // reveal:false — подвоз выбранного СДВИГАЛ список из-под
                    // пальца сразу после тапа, читалось как «не применилось,
                    // жми второй раз» (живой репорт 28.08).
                    StyleEmotions(reveal: false);
                });
                // ИСТОЧНИК, А НЕ ГОТОВАЯ СТРОКА: подпись эмоции переводится, и
                // созданная строкой она оставалась на прежнем языке до тех пор,
                // пока лист не пересоберут (уход на другой экран и обратно) —
                // живой репорт 01.09.
                Lvn.UI.LvnRedress.Bind(chip, () => EmotionLabel(value));
                chip.name = "emo-" + v;
                chip.style.height = 44;
                chip.style.marginBottom = LvnTokens.Space1;
                chip.style.flexShrink = 0;
                chip.style.paddingLeft = LvnTokens.Space3; chip.style.paddingRight = LvnTokens.Space3;
                chip.style.fontSize = LvnTokens.TextXs;
                LvnChrome.Round(chip, LvnTokens.RadiusLg);
                Smooth(chip, LvnMotion.Normal, "background-color", "color");
                _emotions.Add(chip);
            }
            StyleEmotions();
        }

        // reveal — подвезти выбранный чип в кадр: только при перестройке
        // колонки (открытие, смена персонажа), НИКОГДА после тапа.
        private void StyleEmotions(bool reveal = true)
        {
            if (_emotionAxis == null) return;
            LvnWardrobe.Previewed(_entity).TryGetValue(_emotionAxis, out var current);
            if (current == null && _def?.defaults != null)
                _def.defaults.TryGetValue(_emotionAxis, out current);
            foreach (var c in _emotions.contentContainer.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool on = b.name == "emo-" + current;
                SkinButton(b, on);
                if (on && reveal) _emotions.schedule.Execute(() =>
                {
                    if (b.panel != null && b.parent == _emotions.contentContainer)
                        _emotions.ScrollTo(b);
                });
            }
        }
    }
}
