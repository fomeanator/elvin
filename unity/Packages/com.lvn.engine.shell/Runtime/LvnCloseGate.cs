using System.Threading;
using System.Threading.Tasks;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЖДАТЬ, ПОКА ЭКРАН ЗАКРОЮТ — и не остаться ждать навсегда.
    ///
    /// <para>Связка повторялась трижды (база накладных экранов, галерея,
    /// гардероб) и каждый раз из четырёх обязательных частей: создать ожидание
    /// с <c>RunContinuationsAsynchronously</c>, подписать ОТМЕНУ на его
    /// разрешение, дождаться, отпустить подписку. Пропусти любую — и получишь
    /// разное: без флага продолжение выполнится в чужом контексте, без
    /// регистрации отмены экран, закрытый сносом сцены, никогда не вернёт
    /// управление, без освобождения подписки токен утащит за собой ссылку на
    /// мёртвый экран.</para>
    ///
    /// <para>Ни одна из этих ошибок не видна на глаз: экран открывается и
    /// закрывается как обычно, а «повисло» случается через раз и на чужой
    /// машине. Поэтому связка стала объектом, а не образцом для копирования.</para>
    /// </summary>
    internal sealed class LvnCloseGate
    {
        private TaskCompletionSource<bool> _tcs;

        /// <summary>Ждёт закрытия. <c>true</c> — закрыли подтверждением,
        /// <c>false</c> — отменой или сносом.</summary>
        public async Task<bool> WaitAsync(CancellationToken ct)
        {
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => _tcs.TrySetResult(false)))
                return await _tcs.Task;
        }

        /// <summary>Закрыть: разрешает ожидание. Повторный вызов безвреден —
        /// экран закрывают и кнопкой, и тапом мимо, и сносом сцены, иногда
        /// почти одновременно.</summary>
        public void Release(bool confirmed = true) => _tcs?.TrySetResult(confirmed);

        /// <summary>Ждёт ли кто-то прямо сейчас.</summary>
        public bool Waiting => _tcs != null && !_tcs.Task.IsCompleted;
    }
}
