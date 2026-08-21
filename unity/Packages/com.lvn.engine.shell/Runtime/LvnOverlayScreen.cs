using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>Длительность проявления и угасания.</summary>
        protected virtual float FadeSeconds => 0.25f;

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
            await ScreenFx.FadeAsync(this, 0f, 1f, FadeSeconds, ct);
            if (!_open) return false;   // закрыли прямо во время проявления

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            bool confirmed;
            try { confirmed = await _tcs.Task; }
            finally
            {
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
