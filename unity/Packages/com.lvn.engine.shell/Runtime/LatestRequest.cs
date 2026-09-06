using System;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Загрузки завершаются не в порядке запуска: опоздавший результат
    /// не должен подменять более свежий выбор, даже если тот ещё загружается.
    /// </summary>
    internal sealed class LatestRequest
    {
        private readonly object _gate = new object();
        private long _latest;

        public long Begin()
        {
            lock (_gate) return ++_latest;
        }

        public bool TryPublish(long request, Action publish)
        {
            // Проверка и публикация неделимы, иначе новая просьба может
            // вклиниться между ними и получить поверх себя старый результат.
            lock (_gate)
            {
                if (request != _latest) return false;
                publish();
                return true;
            }
        }
    }
}
