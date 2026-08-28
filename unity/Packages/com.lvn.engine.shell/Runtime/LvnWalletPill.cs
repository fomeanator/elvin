using System;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ПЛАШКА КОШЕЛЬКА: значок валюты, сколько её есть и — у восполняемой —
    /// сколько до следующей единицы.
    ///
    /// <para>Элемент один, а собирался он в четырёх экранах своим кодом:
    /// строка состояния, игровой HUD, гардероб, витрина хаба. Разошлись не
    /// только отступы — гардероб писал «13 060 crystals» словом там, где
    /// соседи рисовали кристалл, а обратный отсчёт до восполнения умел только
    /// HUD. Каждая правка кошелька означала обойти все четыре и вспомнить, чем
    /// они отличаются.</para>
    ///
    /// <para>Здесь плашка одна, а отличия экранов — это <see cref="Look"/>:
    /// метрика и цвета. Содержимое, формат числа (<see cref="LvnWallet.Display"/>),
    /// отсчёт и дозапрос баланса общие, потому что это свойства кошелька, а не
    /// экрана.</para>
    /// </summary>
    public sealed class LvnWalletPill : VisualElement
    {
        /// <summary>Как плашка выглядит на ЭТОМ экране. Всё остальное —
        /// поведение — у всех плашек одинаковое.</summary>
        public sealed class Look
        {
            public float Height;                 // 0 — высота по содержимому
            public float PadLeft = 12, PadRight = 12, PadY = 5;
            public float MarginLeft;
            public float Radius = 14f;
            public float IconSize = 22f;
            public float FontSize = 22f;
            public bool Bold;
            public bool Edge;                    // тонкая кромка акцентом (огранка темы)
            public Color Background = new Color(0f, 0f, 0f, 0.4f);
            public Color TextColor = LvnTokens.Text;
            /// <summary>Показывать обратный отсчёт до восполнения (энергия).</summary>
            public bool ShowTimer;
            /// <summary>Что писать в момент, когда восполнение уже наступило,
            /// а свежий баланс ещё едет с сервера.</summary>
            public string TimerReadyText = "…";
            /// <summary>Картинка значка из манифеста; пусто — вектор по смыслу
            /// валюты (<see cref="LvnIcons.ForCurrency"/>).</summary>
            public string IconUrl;
            /// <summary>Цвет вектора; не задан — цвет по смыслу валюты
            /// (энергия акцентом, ценное золотом).</summary>
            public Color? IconTint;
        }

        private readonly string _currency;
        private readonly Look _look;
        private readonly Label _amount;
        private readonly Label _timer;

        public string Currency => _currency;

        public LvnWalletPill(string currency, Look look, ILvnAssets assets = null,
                             Action onTap = null, Action onPlus = null)
        {
            _currency = currency ?? "";
            _look = look ?? new Look();

            pickingMode = onTap != null ? PickingMode.Position : PickingMode.Ignore;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.marginLeft = _look.MarginLeft;
            if (_look.Height > 0f) style.height = _look.Height;
            style.paddingLeft = _look.PadLeft;
            style.paddingRight = _look.PadRight;
            style.paddingTop = _look.PadY;
            style.paddingBottom = _look.PadY;
            style.backgroundColor = _look.Background;
            if (_look.Edge) LvnChrome.Edge(this);
            LvnChrome.Round(this, _look.Radius);

            Add(BuildIcon(assets));

            _amount = new Label(LvnWallet.Display(_currency)) { pickingMode = PickingMode.Ignore };
            _amount.style.color = _look.TextColor;
            _amount.style.fontSize = _look.FontSize;
            if (_look.Bold) _amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(_amount);

            if (_look.ShowTimer)
            {
                _timer = new Label(string.Empty) { pickingMode = PickingMode.Ignore };
                _timer.style.color = _look.TextColor;
                _timer.style.fontSize = Mathf.Max(12f, _look.FontSize - 5f);
                _timer.style.marginLeft = 6;
                _timer.style.opacity = 0.7f;
                _timer.style.display = DisplayStyle.None;
                Add(_timer);
                // Отсчёт тикает сам: экрану не нужно помнить, что у него на
                // баре живёт восполняемая валюта.
                schedule.Execute(Refresh).Every(1000);
            }

            if (onPlus != null) Add(PlusButton(onPlus));

            if (onTap != null)
            {
                RegisterCallback<ClickEvent>(_ => onTap());
                RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            }
            Refresh();
        }

        private VisualElement BuildIcon(ILvnAssets assets)
        {
            if (!string.IsNullOrEmpty(_look.IconUrl))
            {
                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                img.style.width = _look.IconSize; img.style.height = _look.IconSize;
                img.style.marginRight = 6;
                img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                img.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                LvnAsync.Fire(ScreenUi.AssignBgAsync(img, _look.IconUrl, assets), "AssignBg");
                return img;
            }
            // ЗНАЧОК БЕРЁМ У ЦЕННИКА, а не угадываем сами: автор мог назвать
            // его в манифесте (`ui.currency_look[…].icon`), и пилюля этого
            // раньше не видела — в магазине стоял авторский значок, а в строке
            // состояния догаданный. Оттенок так же: свой, если назван.
            var look = LvnPriceTag.Of(_currency);
            var tint = _look.IconTint ?? look.Tint;
            var ic = LvnIcons.Make(look.Icon, _look.IconSize, tint, 0f, LvnTheme.Current.IconGlow);
            ic.pickingMode = PickingMode.Ignore;
            ic.style.marginRight = 6;
            return ic;
        }

        private VisualElement PlusButton(Action onPlus)
        {
            var plus = new Button(onPlus) { text = "+" };
            plus.style.fontSize = _look.FontSize;
            plus.style.marginLeft = 8;
            plus.style.paddingLeft = 10; plus.style.paddingRight = 10;
            plus.style.paddingTop = 1; plus.style.paddingBottom = 1;
            plus.style.color = LvnTokens.OnAccent;
            plus.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Round(plus, Mathf.Max(8f, _look.Radius - 4f));
            return plus;
        }

        /// <summary>Перечитать кошелёк: число и отсчёт. Зовётся сама раз в
        /// секунду, когда показывает отсчёт, и снаружи — на событие кошелька.</summary>
        public void Refresh()
        {
            if (_amount != null) _amount.text = LvnWallet.Display(_currency);
            if (_timer == null) return;

            bool refilling = LvnWallet.Regen.TryGetValue(_currency, out var r)
                             && r.Cap > 0 && r.NextRefillUnix > 0
                             && LvnWallet.Balances.TryGetValue(_currency, out var bal) && bal < r.Cap;
            if (!refilling) { _timer.style.display = DisplayStyle.None; return; }

            long left = r.NextRefillUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (left <= 0)
            {
                // Время пришло, свежий баланс ещё едет — просим его, но не чаще
                // раза в пятнадцать секунд: иначе на нуле бьём сервер каждый тик.
                _timer.text = _look.TimerReadyText ?? "…";
                RequestRefill();
            }
            else _timer.text = FormatDuration(left);
            _timer.style.display = DisplayStyle.Flex;
        }

        private static float _nextRefillRequest;

        private static void RequestRefill()
        {
            // РЕАЛЬНОЕ время, а не часы интерфейса: пауза между запросами к
            // серверу — про сеть, а не про экран. Энергия восполняется, пока
            // игра свёрнута, и «15 секунд» обязаны идти там же.
            if (Time.realtimeSinceStartup < _nextRefillRequest) return;
            _nextRefillRequest = Time.realtimeSinceStartup + 15f;
            LvnAsync.Fire(LvnWallet.RefreshAsync(), "Refresh");
        }

        /// <summary>Сколько осталось: «3:07», а больше часа — «1:12:30».</summary>
        public static string FormatDuration(long seconds)
        {
            long h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        }
    }
}
