using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЕДИНЫЙ НАВБАР приложения (решение Ильи 26.08): один верхний бар и в
    /// меню, и в игре — вместо «мусорки из разных навбаров у всех» (пилюли
    /// хаба, фабы сцены, отдельный кружок). Полупрозрачная подложка с нижней
    /// кромкой; слева лого «Т» в кружке (вектор кодом), центр отдан кружку
    /// загрузок (DownloadHud — отдельный оверлей, позиционируется в эту же
    /// строку), справа валюты БЕЗ «+» (тап по пилюле открывает магазин) и
    /// бургер.
    ///
    /// <para>ИГРОВОЙ РЕЖИМ: в сцене бар скрыт полностью (чистый кадр) и
    /// появляется тремя путями — тапом по верхней кромке (невидимая
    /// полоска-ловушка), САМИМ СОБЫТИЕМ (пошла загрузка/пропала сеть — «а как
    /// же по событиям»), и прячется через паузу тишины.</para>
    ///
    /// <para>ШТОРКИ/ЧЁЛКИ: бар отступает на высоту safe area (хост кормит
    /// <see cref="SetSafeTop"/>), поэтому вырез камеры всегда ВЫШЕ бара и
    /// центр строки безопасен — как делают все крупные мобильные игры.</para>
    /// </summary>
    public sealed class LvnTopBar : VisualElement
    {
        private const float RowH = 64f;

        /// <summary>Валюты пилюль (id кошелька), порядок = порядок на баре.</summary>
        public List<string> Currencies = new List<string>();
        /// <summary>Тап по пилюле валюты — хост открывает магазин.</summary>
        public Action<string> OnCurrency;
        /// <summary>Бургер: в сцене — квик-меню, в меню — настройки.</summary>
        public Action OnBurger;

        private readonly VisualElement _row;
        private readonly VisualElement _pills;
        private readonly VisualElement _tapCatcher;
        private bool _inGame;
        private bool _held;              // показан событием (работа идёт)
        private float _tapShownAt = -1f; // показан тапом — до таймера
        private float _safeTop;

        public LvnTopBar()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0;

            // Ловушка тапа по верхней кромке — активна только в игре при
            // скрытом баре. Ниже её 48 юнитов сцена живёт как обычно.
            _tapCatcher = new VisualElement();
            _tapCatcher.style.position = Position.Absolute;
            _tapCatcher.style.left = 0; _tapCatcher.style.right = 0;
            _tapCatcher.style.top = 0; _tapCatcher.style.height = 48;
            _tapCatcher.style.display = DisplayStyle.None;
            _tapCatcher.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                ShowTemporarily();
            });
            Add(_tapCatcher);

            _row = new VisualElement();
            var bg = LvnTokens.PanelBg;
            _row.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.62f);
            _row.style.borderBottomWidth = 1f;
            _row.style.borderBottomColor = LvnTokens.Border;
            _row.style.height = RowH;
            _row.style.flexDirection = FlexDirection.Row;
            _row.style.alignItems = Align.Center;
            _row.style.paddingLeft = 12; _row.style.paddingRight = 12;
            Add(_row);

            _row.Add(Logo());

            var spacer = new VisualElement();
            spacer.pickingMode = PickingMode.Ignore;
            spacer.style.flexGrow = 1;
            _row.Add(spacer);

            _pills = new VisualElement();
            _pills.style.flexDirection = FlexDirection.Row;
            _pills.style.alignItems = Align.Center;
            _row.Add(_pills);

            _row.Add(Burger());

            RefreshBalances();
        }

        // ── содержимое ────────────────────────────────────────────────────────

        // Лого: «Т» в акцентном кружке — вектор кодом, без ассетов.
        private VisualElement Logo()
        {
            var circle = new VisualElement();
            circle.pickingMode = PickingMode.Ignore;
            circle.style.width = 42; circle.style.height = 42;
            LvnChrome.Round(circle, 21f);
            circle.style.backgroundColor = LvnTokens.Accent;
            circle.style.alignItems = Align.Center;
            circle.style.justifyContent = Justify.Center;
            var t = new Label("Т");
            t.pickingMode = PickingMode.Ignore;
            t.style.color = LvnTokens.OnAccent;
            t.style.fontSize = 26;
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            circle.Add(t);
            return circle;
        }

        // Бургер — три полоски (глиф «☰» на Android — tofu, грабля уже ловлена).
        private VisualElement Burger()
        {
            var b = new VisualElement();
            b.style.width = 46; b.style.height = 46;
            b.style.marginLeft = 10;
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            LvnChrome.Round(b, 12f);
            b.style.backgroundColor = LvnTokens.Faint;
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement();
                bar.pickingMode = PickingMode.Ignore;
                bar.style.width = 20; bar.style.height = 2.5f;
                bar.style.marginTop = i == 0 ? 0 : 4;
                bar.style.backgroundColor = LvnTokens.Text;
                b.Add(bar);
            }
            b.RegisterCallback<ClickEvent>(_ => OnBurger?.Invoke());
            b.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            return b;
        }

        /// <summary>Перерисовать пилюли валют из живого кошелька. Без «+» —
        /// тап по самой пилюле открывает магазин.</summary>
        public void RefreshBalances()
        {
            _pills.Clear();
            foreach (var cur in Currencies)
            {
                var pill = new VisualElement();
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.marginLeft = 8;
                pill.style.height = 40;
                pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
                pill.style.backgroundColor = LvnTokens.Faint;
                LvnChrome.Edge(pill);
                LvnChrome.Round(pill, 20f);

                bool energy = cur == "energy";
                var ic = LvnIcons.Make(energy ? LvnIcon.Energy : LvnIcon.Gem, 19f,
                    energy ? LvnTokens.Accent : LvnTokens.Gold, 0f, LvnTheme.Current.IconGlow);
                ic.pickingMode = PickingMode.Ignore;
                ic.style.marginRight = 6;
                pill.Add(ic);

                long bal = Lvn.Services.LvnWallet.Balances.TryGetValue(cur, out var b) ? b : 0;
                string text = Lvn.Services.LvnWallet.Regen.TryGetValue(cur, out var r) && r.Cap > 0 && bal < r.Cap
                    ? $"{bal}/{r.Cap}" : bal.ToString("N0");
                var lbl = new Label(text);
                lbl.pickingMode = PickingMode.Ignore;
                lbl.style.color = LvnTokens.Text;
                lbl.style.fontSize = 20;
                lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
                pill.Add(lbl);

                var captured = cur;
                pill.RegisterCallback<ClickEvent>(_ => OnCurrency?.Invoke(captured));
                pill.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
                _pills.Add(pill);
            }
        }

        // ── режимы ────────────────────────────────────────────────────────────

        /// <summary>Высота безопасной зоны сверху в юнитах панели — бар и его
        /// содержимое опускаются ПОД вырез камеры.</summary>
        public void SetSafeTop(float units)
        {
            if (Mathf.Approximately(_safeTop, units)) return;
            _safeTop = units;
            _row.style.marginTop = units;
            _tapCatcher.style.height = 48 + units;
        }

        /// <summary>Игровой режим: бар скрыт (чистый кадр), ловушка тапа активна.</summary>
        public void SetInGame(bool inGame)
        {
            if (_inGame == inGame) return;
            _inGame = inGame;
            _held = false;
            _tapShownAt = -1f;
            ApplyVisibility(animated: false);
        }

        /// <summary>Событие держит бар на экране (загрузка/офлайн/синк идёт).</summary>
        public void NotifyWork(bool hasWork)
        {
            if (_held == hasWork) return;
            _held = hasWork;
            ApplyVisibility(animated: true);
        }

        /// <summary>Показать по тапу на несколько секунд.</summary>
        public void ShowTemporarily()
        {
            _tapShownAt = Time.realtimeSinceStartup;
            ApplyVisibility(animated: true);
            schedule.Execute(() =>
            {
                if (_tapShownAt > 0f && Time.realtimeSinceStartup - _tapShownAt >= 3.9f)
                {
                    _tapShownAt = -1f;
                    ApplyVisibility(animated: true);
                }
            }).ExecuteLater(4000);
        }

        private bool BarVisible => !_inGame || _held || _tapShownAt > 0f;

        private void ApplyVisibility(bool animated)
        {
            bool show = BarVisible;
            _tapCatcher.style.display = _inGame && !show ? DisplayStyle.Flex : DisplayStyle.None;
            float hidden = -(RowH + _safeTop + 4f);
            if (!animated)
            {
                _row.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                _row.style.translate = new Translate(0f, show ? 0f : hidden);
                return;
            }
            // Выезд/уход строкой сверху — тем же темпом, что попапы (~0,2 с).
            _row.style.display = DisplayStyle.Flex;
            float from = show ? hidden : 0f, to = show ? 0f : hidden;
            _row.experimental.animation.Start(0f, 1f, 200, (r, p) =>
            {
                float e = 1f - Mathf.Pow(1f - p, 3f);
                r.style.translate = new Translate(0f, Mathf.Lerp(from, to, e));
                if (p >= 1f && !show) r.style.display = DisplayStyle.None;
            });
        }
    }
}
