using System;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// АТМОСФЕРА МЕНЮ — один живой фон под всеми экранами оболочки.
    ///
    /// <para>Решение Ильи от 26.08: не по картинке на экран, а ОДНО полотно
    /// шириной в четыре экрана, которое медленно дышит и едет вместе с
    /// вкладками. Тогда переход между разделами читается как поворот головы, а
    /// не как смена декорации.</para>
    ///
    /// <para>Здесь же правило видимости: атмосфера жива, пока на экране меню, и
    /// мертва, когда меню рисует СЦЕНА. Правило это стоило живого репорта —
    /// «гардероб сломан»: атмосфера с событийной подпиской оставалась поверх и
    /// заслоняла его целиком.</para>
    /// </summary>
    public sealed partial class NovelShell
    {
        private VisualElement _atmosphere;
        private bool _sceneMenu; // меню рисуется сценой: оболочка прозрачна

        private void BuildAtmosphere()
        {
            _atmosphere?.RemoveFromHierarchy();
            var t = LvnTheme.Current;
            // ПОЛОТНО В 4 ЭКРАНА (концепция Ильи и партнёра): один большой фон
            // по горизонтали; каждая вкладка меню смотрит в свою четверть,
            // переезд вкладок плавно везёт полотно (TabGoTo). Пока полотно —
            // атмосфера темы; арт-полотно партнёра ляжет сюда же данными.
            _atmosphere = new VisualElement { pickingMode = PickingMode.Ignore };
            _atmosphere.style.position = Position.Absolute;
            _atmosphere.style.left = 0; _atmosphere.style.top = 0; _atmosphere.style.bottom = 0;
            // ПАРАЛЛАКС-ГЛУБИНА (уточнение Ильи): фон ОДИН, шириной 160%
            // экрана — за вкладку он сдвигается на долю (излишек ширины /3),
            // отставая от страниц: страницы едут на экран, фон — на пятую.
            _atmosphere.style.width = Length.Percent(125f); // запас под сдвиг 3×0.067W
            _atmosphere.style.backgroundColor = t.Bg;
            var canvasUrl = _manifest?.ui?.browse?.canvas;
            _sceneMenu = !string.IsNullOrEmpty(canvasUrl);
            bool sceneMenu = _sceneMenu;
            if (sceneMenu)
            {
                // МЕНЮ ВНУТРИ ИГРЫ: полотно и героиню рисует СЦЕНА (канвас под
                // панелью) — оболочка прозрачна, атмосфера мертва совсем.
                _atmosphere.style.display = DisplayStyle.None;
                _atmosphere.style.backgroundColor = Color.clear;
            }
            else if (!sceneMenu && canvasUrl != null)
            {
                // Арт-полотно партнёра: фото на всю ширину 4 экранов + тёмная
                // вуаль (текст обязан читаться) + тинт вкладки поверх.
                LvnPicture.Layer(_atmosphere, canvasUrl, _assets, what: "MenuCanvas");
                var veil = new VisualElement { pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(veil);
                // «Реализм» (Илья): фото почти как есть — лишь лёгкая вуаль,
                // чтобы текст поверх оставался читабельным.
                veil.style.backgroundColor = UiColor.WithAlpha(t.Bg, 0.22f);
                _atmosphere.Add(veil);
                _canvasTint = new VisualElement { pickingMode = PickingMode.Ignore };
                LvnChrome.Stretch(_canvasTint);
                _atmosphere.Add(_canvasTint);
            }
            else LvnBackdrop.Apply(_atmosphere, t);
            _root.Insert(0, _atmosphere);

            // ГЕРОИНЮ РИСУЕТ СЦЕНА — ВСЕГДА И ТОЛЬКО ОНА. Здесь стояла вторая
            // кукла: те же слои, собранные оболочкой в UI-элементе. Двух
            // реализаций одного человека хватало, чтобы каждый вопрос «почему
            // она такая» начинался с «а кто её сейчас рисует», и чтобы всякая
            // правка делалась дважды. Фигура одна, дом у неё один (VnStage).
            // ВИДИМОСТЬ ПО ПРАВИЛУ «виден экран меню», а не «нет главы»:
            // гардероб из хаба прячет хаб и живёт в документе СЦЕНЫ — атмосфера
            // с событийной подпиской оставалась поверх и заслоняла его целиком
            // (живой скрин «гардероб сломан»). Тик ниже держит правило сам.
            _root.schedule.Execute(() =>
            {
                if (_sceneMenu) return; // сцена рисует меню — атмосфера мертва
                bool menuVisible =
                    (Boot != null && Boot.style.display == DisplayStyle.Flex) ||
                    (Browse != null && Browse.View.style.display == DisplayStyle.Flex);
                var want = menuVisible ? DisplayStyle.Flex : DisplayStyle.None;
                if (_atmosphere.style.display != want) _atmosphere.style.display = want;
            }).Every(100);

            // Параллакс: постоянный медленный дрейф (фон ЖИВЁТ сам), плюс
            // скролл ленты хаба и наклон телефона; слои — на разной глубине.
            var layers = new System.Collections.Generic.List<VisualElement>();
            _atmosphere.Query<VisualElement>("lvn-backdrop").ForEach(layers.Add);
            Vector2 tilt = Vector2.zero;
            _root.schedule.Execute(() =>
            {
                if (_atmosphere.style.display == DisplayStyle.None) return;
                float time = Lvn.LvnClock.Now();
                float scroll = Hub != null && Hub.style.display == DisplayStyle.Flex ? Hub.ScrollY : 0f;
                var acc = UnityEngine.Input.acceleration;
                var target = new Vector2(
                    Mathf.Clamp(acc.x, -0.5f, 0.5f),
                    Mathf.Clamp(acc.y + 0.8f, -0.5f, 0.5f));
                tilt = Vector2.Lerp(tilt, target, 0.06f);
                // ПОЛОТНО ЕДЕТ ВСЕГДА (грабля: с фото-артом слоёв нет, и ранний
                // выход оставлял его неподвижным при переездах вкладок).
                _atmosphere.style.translate = new Translate(-_tabCanvasX, 0f);
                for (int i = 0; i < layers.Count; i++)
                {
                    // Сумма сдвигов ОБЯЗАНА жить в напуске слоя (80px), иначе
                    // у кромки экрана оголяется шов: глубина ограничена тремя
                    // ступенями, вклад скролла закэмплен.
                    float k = Mathf.Min(i + 1, 3);
                    float driftX = Mathf.Sin(time * 0.11f + i * 1.7f) * 6f * k;
                    float driftY = Mathf.Cos(time * 0.09f + i * 2.3f) * 5f * k;
                    float scrollY = Mathf.Clamp(scroll * (0.05f + 0.045f * i), 0f, 30f);
                    layers[i].style.translate = new Translate(
                        driftX + tilt.x * 8f * k,
                        driftY - scrollY + tilt.y * 6f * k);
                }
            }).Every(33);
        }
    }
}
