using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// НАКЛАДНОЙ ЭКРАН — общий жизненный цикл: проявиться, дождаться закрытия,
    /// погаснуть.
    ///
    /// <para>Восемь экранов (профиль, настройки, магазин, наборы, скины,
    /// лидерборд, ежедневные награды, деталь новеллы) держали этот цикл
    /// СВОЕЙ копией — в коде даже стояло «mirrors StoreScreen». Вместе с ним
    /// копировались `Round`, `ClearBorder`, `Hide` и поля состояния: без общего
    /// предка у каждого экрана заводится собственная версия одного и того же,
    /// и однажды одна из них расходится.</para>
    ///
    /// <para>Тонкость, ради которой цикл и вынесен целиком: <see cref="Hide"/>,
    /// вызванный ВО ВРЕМЯ проявления, обязан отменить открытие. Иначе ожидание
    /// остаётся висеть на обещании, которое никто уже не выполнит, — экран
    /// закрыт, а вызвавший его код ждёт вечно.</para>
    /// </summary>
    public abstract class LvnOverlayScreen : VisualElement
    {
        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private VisualElement _sheet;

        /// <summary>Длительность проявления и угасания — В ТЕМП АКТЁРОВ:
        /// фактический вход персонажа ~0,2 с (0.35 × шкалы), и попапы дышат
        /// той же длительностью — «дорого» читается именно из согласованности
        /// (решение Ильи 25.08).</summary>
        protected virtual float FadeSeconds => 0.2f;

        /// <summary>ЕДИНЫЙ ВРАППЕР ЛИСТА (решение Ильи 25.08): накладные
        /// экраны — эдакие попапы, а не экраны, и выглядели «коротким
        /// контентом на тёмном полотне». Наследник отдаёт сюда свой лист —
        /// и получает общий вид (стекло UiGlass, окантовка с акцентной
        /// верхней кромкой) и общую хореографию: скрим фейдится, лист
        /// подъезжает снизу со scale — это делает ShowAsync сам.</summary>
        protected void AdoptSheet(VisualElement sheet)
        {
            _sheet = sheet;
            if (sheet == null) return;
            var bg = LvnTokens.PanelBg;
            sheet.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.6f);
            UiGlass.Apply(sheet, 0.55f, new Color(bg.r, bg.g, bg.b, 0.72f));
            LvnChrome.Edge(sheet);
            LvnChrome.Round(sheet, LvnTokens.Radius + 6f);
            // Акцентная кромка сверху — «крышка» попапа: даёт листу край,
            // которого не хватало на тёмном полотне.
            sheet.style.borderTopWidth = 2.5f;
            sheet.style.borderTopColor = LvnTokens.Accent;
        }

        // Хореография листа: подъезд снизу + лёгкий scale. Скрим (сам экран)
        // фейдится параллельно базовым FadeAsync; ждать лист отдельно не надо —
        // длительность одна.
        private void PlaySheet(bool opening)
        {
            var s = _sheet;
            if (s == null) return;
            int ms = Mathf.RoundToInt(FadeSeconds * 1000f * (opening ? 1f : 0.8f));
            s.experimental.animation.Start(0f, 1f, Mathf.Max(1, ms), (_, p) =>
            {
                float e = 1f - Mathf.Pow(1f - p, 3f);
                float k = opening ? e : 1f - e;
                s.style.translate = new Translate(0f, Mathf.Lerp(26f, 0f, k));
                float sc = Mathf.Lerp(0.965f, 1f, k);
                s.style.scale = new Scale(new Vector2(sc, sc));
            });
        }

        /// <summary>Открыт ли экран сейчас.</summary>
        protected bool IsOpen => _open;

        /// <summary>Открыть и ждать закрытия. Возвращает true, если закрыли
        /// подтверждением (<see cref="Close"/>), и false — если отменой.</summary>
        public async Task<bool> ShowAsync(CancellationToken ct = default)
        {
            if (_open) return false;
            _open = true;
            style.display = DisplayStyle.Flex;
            OnOpening();
            PlaySheet(opening: true);
            await ScreenFx.FadeAsync(this, 0f, 1f, FadeSeconds, ct);
            if (!_open) return false;   // закрыли прямо во время проявления

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            bool confirmed;
            try { confirmed = await _tcs.Task; }
            finally
            {
                PlaySheet(opening: false);
                await ScreenFx.FadeAsync(this, 1f, 0f, FadeSeconds, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
                OnClosed();
            }
            return confirmed;
        }

        /// <summary>Убрать немедленно, без угасания: смена главы, выход в меню.</summary>
        public virtual void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        /// <summary>
        /// Закрыть ПОДТВЕРЖДЕНИЕМ — тем, чего экран и ждал: «играть», «купить»,
        /// «сохранить».
        ///
        /// <para>⚠️ Смысл противоположен прежнему одноимённому методу экранов:
        /// там <c>Close</c> означал «уйти ни с чем» и возвращал false. При
        /// переводе экрана детали на этот класс кнопка «назад» продолжала звать
        /// Close — и стала бы ЗАПУСКАТЬ игру. Поймано глазами; автотестом это не
        /// ловится: асинхронный цикл в EditMode не прокручивается ни блокирующим
        /// ожиданием (дедлок главного потока), ни покадровым (нет кадров).
        /// Поэтому — отмена всегда через <see cref="Cancel"/>.</para>
        /// </summary>
        protected void Close() => _tcs?.TrySetResult(true);

        /// <summary>Отменить: крестик, «назад», системная кнопка возврата.</summary>
        protected void Cancel() => _tcs?.TrySetResult(false);

        /// <summary>Наследник может подготовить данные перед проявлением.</summary>
        protected virtual void OnOpening() { }

        /// <summary>И прибраться после угасания.</summary>
        protected virtual void OnClosed() { }
    }
}
