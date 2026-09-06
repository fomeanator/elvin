using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Пока работа ждёт сеть, новые события означают один повтор: очередь
    /// одинаковых запросов лишь скачала бы одно и то же несколько раз.
    /// </summary>
    internal sealed class CoalescingWork
    {
        private readonly Func<Task> _work;
        private readonly object _gate = new object();
        private bool _running;
        private bool _pending;

        public CoalescingWork(Func<Task> work)
        {
            _work = work ?? throw new ArgumentNullException(nameof(work));
        }

        // Первый вызов ждёт весь цикл и сообщает его ошибку; остальные только
        // оставляют просьбу, чтобы одно падение не логировалось на каждый опрос.
        public async Task RequestAsync()
        {
            lock (_gate)
            {
                if (_running)
                {
                    _pending = true;
                    return;
                }
                _running = true;
            }

            ExceptionDispatchInfo failure = null;
            while (true)
            {
                // Продолжение остаётся в контексте вызывающего: работа оболочки
                // после сети обращается к Unity и не может уйти в пул потоков.
                try { await _work(); }
                catch (Exception ex)
                {
                    // Ошибка не должна терять уже запрошенный повтор. Первую
                    // отдаём вызывающему после него, сохраняя исходный стек.
                    if (failure == null) failure = ExceptionDispatchInfo.Capture(ex);
                }

                lock (_gate)
                {
                    if (_pending)
                    {
                        _pending = false;
                        continue;
                    }
                    // Проверка повтора и освобождение неделимы: иначе просьба
                    // на этой границе останется без исполнителя.
                    _running = false;
                    break;
                }
            }
            failure?.Throw();
        }
    }
}
