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

        /// <summary>
        /// ОТКРЫТЬ ЗАНОВО, ОТПУСТИВ ПРОШЛОГО ЖДУЩЕГО.
        ///
        /// <para>Экран умеют показать второй раз, не закрыв первый: тап по двум
        /// карточкам подряд, ссылка поверх открытого листа, возврат из
        /// соседнего экрана. Прежний ждущий обязан получить ответ — иначе он не
        /// получит его НИКОГДА, а вместе с ним останется висеть его цикл:
        /// глава, покупка, выбор наряда.</para>
        ///
        /// <para>Отдельным именем, а не внутри <see cref="WaitAsync"/>,
        /// намеренно: экраны, у которых повторный показ невозможен по
        /// устройству (модальный лист держит признак «открыт»), ждут обычным
        /// способом, и молчаливое «отпустить кого-то» в общем пути было бы для
        /// них сюрпризом.</para>
        ///
        /// <para>ОТДАЁТ ОЖИДАНИЕ НАПРЯМУЮ, без async-обёртки, и это не мелочь.
        /// Обёртка сдвигает ответ на шаг: обещание разрешено, а задача снаружи
        /// ещё не завершена. Для ворот, которые разрешают НАЖАТИЕМ, разница
        /// видна — цикл глав получал бы ответ кадром позже, а тот, кто
        /// спрашивает результат сразу, вставал бы намертво: продолжение ждёт
        /// главный поток, а главный поток ждёт продолжения.</para>
        /// </summary>
        public Task<bool> ReopenAsync(CancellationToken ct = default)
        {
            Release(false);
            var tcs = _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.CanBeCanceled)
            {
                var reg = ct.Register(() => tcs.TrySetResult(false));
                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            }
            return tcs.Task;
        }

        /// <summary>Закрыть: разрешает ожидание. Повторный вызов безвреден —
        /// экран закрывают и кнопкой, и тапом мимо, и сносом сцены, иногда
        /// почти одновременно.</summary>
        public void Release(bool confirmed = true) => _tcs?.TrySetResult(confirmed);

        /// <summary>Ждёт ли кто-то прямо сейчас.</summary>
        public bool Waiting => _tcs != null && !_tcs.Task.IsCompleted;
    }
}
