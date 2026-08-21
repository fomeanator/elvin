using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// ЗАПУСК БЕЗ ОЖИДАНИЯ, НО НЕ БЕЗ ПРИСМОТРА.
    ///
    /// <para>Фоновая работа — прогрев библиотеки, отправка события, подгрузка
    /// следующей главы — запускается и не ждётся: игре нельзя стоять. Обычная
    /// запись для этого, <c>_ = ЧтоТоAsync()</c>, имеет скверное свойство:
    /// упавшая задача исчезает бесследно. Ни строки в логе, ни следа на
    /// устройстве — только симптом вроде «фон иногда не появляется», который
    /// потом ищут неделю.</para>
    ///
    /// <para><see cref="Fire"/> делает то же самое, но упавшую задачу называет
    /// вслух: что именно не получилось и почему. Отмена — не ошибка: смена
    /// главы и снос экрана отменяют работу пачками, и шуметь об этом нельзя.</para>
    /// </summary>
    public static class LvnAsync
    {
        /// <summary>Запустить и забыть — но с именем, которое попадёт в лог,
        /// если работа упадёт.</summary>
        public static void Fire(Task task, string what)
        {
            if (task == null) return;
            _ = Watch(task, what);
        }

        private static async Task Watch(Task task, string what)
        {
            try { await task; }
            catch (OperationCanceledException) { /* отмена — обычный конец фоновой работы */ }
            catch (Exception ex)
            {
                // Одна строка, а не стек целиком: на устройстве важнее ЧТО
                // сорвалось, а стек фоновой задачи почти всегда один и тот же.
                Debug.LogWarning($"[lvn-async] «{what}» не удалось: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
