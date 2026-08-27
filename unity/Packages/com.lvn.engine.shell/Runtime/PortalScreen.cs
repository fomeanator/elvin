using System;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СЦЕНА ПЕРЕХОДА — вход в главу как событие мира.
    ///
    /// <para>На месте экрана загрузки: героиня стоит у створа, снизу — что за
    /// миссия и насколько створ готов. «Готовность портала» и есть прогресс
    /// загрузки, только названный на языке игры: ожидание перестаёт быть
    /// техническим и становится частью истории.</para>
    ///
    /// <para>Экран рисует ТОЛЬКО нижнюю панель и держит своё состояние. Сцену
    /// за ним — героиню и сам створ — ведёт хост: это его сцена, и она
    /// непрерывна (см. <c>VnStage.HandOver</c>). Экран лишь сообщает,
    /// насколько створ готов (<see cref="Readiness"/>), и что игрок решил
    /// войти.</para>
    ///
    /// <para>Фон намеренно прозрачен: за панелью видна живая сцена, иначе
    /// переход снова превратился бы в заслонку поверх мира.</para>
    /// </summary>
    public sealed class PortalScreen : LvnOverlayScreen
    {
        private readonly PortalConfig _cfg;
        private readonly Label _title, _subtitle, _status;
        private readonly VisualElement _track, _fill;
        private readonly Button _enter;

        private TaskCompletionSource<bool> _entered;

        /// <summary>Насколько створ готов (0..1) — хост двигает по этому портал
        /// на сцене. Зовётся только на ИЗМЕНЕНИЕ, а не каждый кадр.</summary>
        public Action<float> Readiness;

        public PortalScreen(PortalConfig cfg)
        {
            _cfg = cfg ?? new PortalConfig();

            // Панель прижата к низу: верх кадра принадлежит сцене — там стоят
            // героиня и створ, и закрывать их нечем.
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = 0; panel.style.right = 0; panel.style.bottom = 0;
            panel.style.paddingLeft = LvnTokens.Space4;
            panel.style.paddingRight = LvnTokens.Space4;
            panel.style.paddingTop = LvnTokens.Space4;
            panel.style.paddingBottom = LvnTokens.Space5;
            panel.style.backgroundColor = LvnTokens.Veil(0.62f);
            LvnChrome.Edge(panel, 0.7f);
            Add(panel);

            var eyebrow = new Label(LvnTheme.Current.Heading(
                string.IsNullOrEmpty(_cfg.title_label) ? "Переход" : _cfg.title_label));
            eyebrow.style.fontSize = LvnTokens.TextXs;
            eyebrow.style.color = LvnTokens.Accent;
            eyebrow.style.letterSpacing = LvnTheme.Current.Tracking;
            panel.Add(eyebrow);

            _title = new Label { text = "" };
            _title.style.fontSize = LvnTokens.TextLg;
            _title.style.color = LvnTokens.Text;
            _title.style.whiteSpace = WhiteSpace.Normal;
            _title.style.marginTop = LvnTokens.Space1;
            panel.Add(_title);

            _subtitle = new Label { text = "" };
            _subtitle.style.fontSize = LvnTokens.TextSm;
            _subtitle.style.color = LvnTokens.TextDim;
            _subtitle.style.marginTop = 2;
            panel.Add(_subtitle);

            // Полоса готовности створа — та же шкала, что везде в оболочке
            // (роль знает стилизатор), только названа по-другому.
            _track = LvnStyler.Track(new VisualElement(), 10f, LvnTokens.SurfaceHi);
            _track.style.marginTop = LvnTokens.Space3;
            _fill = LvnStyler.Fill(new VisualElement(), 5f);
            _fill.style.height = Length.Percent(100f);
            _fill.style.width = Length.Percent(0f);
            _track.Add(_fill);
            panel.Add(_track);

            _status = new Label { text = "" };
            _status.style.fontSize = LvnTokens.TextXs;
            _status.style.color = LvnTokens.TextDim;
            _status.style.marginTop = LvnTokens.Space1;
            panel.Add(_status);

            _enter = new Button(() => _entered?.TrySetResult(true))
            {
                text = string.IsNullOrEmpty(_cfg.enter_label) ? "Войти" : _cfg.enter_label,
            };
            _enter.style.fontSize = LvnTokens.TextBase;
            _enter.style.marginTop = LvnTokens.Space3;
            _enter.style.paddingTop = 16; _enter.style.paddingBottom = 16;
            panel.Add(_enter);
            SetReady(false);
        }

        /// <summary>Кнопка честна: пока створ не готов, войти нельзя — и это
        /// видно, а не выясняется по бездействию тапа.</summary>
        private void SetReady(bool ready)
        {
            _enter.SetEnabled(ready);
            if (ready) LvnStyler.Primary(_enter);
            else LvnStyler.Quiet(_enter);
            _enter.style.opacity = ready ? 1f : 0.55f;
        }

        /// <summary>
        /// Показать переход и ждать, пока игрок войдёт.
        ///
        /// <para><paramref name="isReady"/> — готова ли глава (тот же признак,
        /// что гейтил экран загрузки), <paramref name="progress"/> — доля
        /// скачанного. Возвращает false, если экран сняли (отмена).</para>
        /// </summary>
        public async Task<bool> RunAsync(string mission, string subtitle,
            Func<bool> isReady, Func<float> progress, bool locked = false,
            CancellationToken ct = default)
        {
            ScreenUi.SetText(_title, mission ?? "");
            ScreenUi.SetText(_subtitle, subtitle ?? "");
            _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            style.display = DisplayStyle.Flex;
            style.opacity = 1f;

            float shown = -1f;
            bool wasReady = false;
            SetReady(false);
            Readiness?.Invoke(Idle);

            using var reg = ct.Register(() => _entered?.TrySetResult(false));
            while (!_entered.Task.IsCompleted)
            {
                // Недоступная миссия НЕ «грузится вечно»: створ стоит
                // тусклым, кнопка мертва, и написано почему. Загрузка и
                // недоступность — разные вещи, и путать их в одном
                // неопределённом ожидании нечестно.
                bool ready = !locked && (isReady?.Invoke() ?? true);
                float p = locked ? 0f : (ready ? 1f : Mathf.Clamp01(progress?.Invoke() ?? 0f));
                if (!Mathf.Approximately(p, shown))
                {
                    shown = p;
                    _fill.style.width = Length.Percent(p * 100f);
                    // Створ заряжается ВМЕСТЕ с полосой: он и есть индикатор,
                    // а полоса — его расшифровка для тех, кто считает в
                    // процентах.
                    Readiness?.Invoke(Mathf.Lerp(Idle, 1f, p));
                    ScreenUi.SetText(_status,
                        locked ? (string.IsNullOrEmpty(_cfg.locked_label) ? "Створ закрыт" : _cfg.locked_label)
                        : ready ? "Створ открыт"
                        : $"{(string.IsNullOrEmpty(_cfg.waiting_label) ? "Створ заряжается" : _cfg.waiting_label)} — {Mathf.RoundToInt(p * 100f)}%");
                }
                if (ready != wasReady) { wasReady = ready; SetReady(ready); }
                await Task.Yield();
            }
            return _entered.Task.Result;
        }

        /// <summary>Насколько створ виден, пока глава не готова.</summary>
        public float Idle => Mathf.Clamp01(_cfg.idle ?? 0.34f);

        /// <summary>Где стоит створ и во что раскрывается — хост ставит по
        /// этим числам сам эффект (<c>fx portal</c>).</summary>
        public float CenterX => _cfg.x ?? 0.72f;
        public float CenterY => _cfg.y ?? 0.52f;
        public float Radius => _cfg.radius ?? 0.30f;
        public string Color => _cfg.color;

        /// <summary>Рост и место героини у створа: рядом с ним важен масштаб
        /// перехода, поэтому она заметно мельче, чем в витрине.</summary>
        public float DollHeight => _cfg.doll_height ?? 0.30f;
        public float DollX => _cfg.doll_x ?? 0.34f;
    }
}
