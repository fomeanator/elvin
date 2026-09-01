using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Lvn
{
    /// <summary>
    /// ДОЖДАТЬСЯ ОТВЕТА СЕТИ — цикл ожидания, один на все запросы.
    ///
    /// <para>Цикл выглядит на три строки, и поэтому его писали заново каждый
    /// раз: «пока не готово — проверить отмену и уступить кадр». Копий вышло
    /// четыре: два в загрузчике контента, по одной в хранилище состояния и в
    /// файловых ассетах.</para>
    ///
    /// <para>Разошлись они там, где это дороже всего. Загрузчик контента знает
    /// про ЗАСТОЙ: срок отсчитывается не с начала запроса, а с последнего
    /// пришедшего байта, потому что фон в 5 МБ на медленной соте законно едет
    /// дольше любого таймаута, а мёртв только замерший счётчик (живой случай на
    /// BlueStacks: «Request timeout» каждые несколько секунд). Хранилище
    /// состояния и файловые ассеты этой защиты не получили вовсе — их запросы
    /// висели, пока не сработает срок UnityWebRequest, а он про весь ответ, а
    /// не про молчание.</para>
    ///
    /// <para>Отмена здесь всегда обрывает запрос (<c>Abort</c>) и отвечает
    /// <c>false</c>. Что делать дальше — вернуть пусто или бросить
    /// исключение — решает вызывающий: у загрузчика провал обязан всплыть,
    /// а у необязательного звука — нет.</para>
    /// </summary>
    public static class LvnNetWait
    {
        /// <summary>
        /// ДОЖДАТЬСЯ ЛЮБОЙ ОПЕРАЦИИ UNITY — по событию, без опроса.
        ///
        /// <para>Загрузка набора с диска (<c>AssetBundle.LoadFromFileAsync</c>)
        /// и выемка из него объекта — тоже операции, и их тоже ждали циклом
        /// «пока не готово — уступи кадр». Прогресса у них никто не
        /// показывает, значит каждый оборот цикла — потраченный кадр на ровном
        /// месте.</para>
        ///
        /// <para>Дом переехал в ядро: сетевые запросы шлют и продуктовые
        /// службы, а сборки контента они не видят — оттуда и пятая копия
        /// цикла.</para>
        /// </summary>
        public static async Task DoneAsync(UnityEngine.AsyncOperation op, CancellationToken ct = default)
        {
            if (op == null) return;
            if (!op.isDone)
            {
                var tcs = new TaskCompletionSource<bool>();
                op.completed += _ => tcs.TrySetResult(true);
                using (ct.CanBeCanceled ? ct.Register(() => tcs.TrySetResult(false)) : default)
                    await tcs.Task;
            }
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Ждать, пока запрос не завершится, не замолчит или не будет отменён.
        /// </summary>
        /// <param name="req">запрос, чтобы оборвать его при отмене и застое</param>
        /// <param name="op">операция отправки, за которой ждём</param>
        /// <param name="ct">отмена вызывающего</param>
        /// <param name="stallSeconds">сколько терпеть МОЛЧАНИЕ (0 — срок из
        /// <see cref="LvnNetPatience.StallSeconds"/>)</param>
        /// <param name="onProgress">сколько байт пришло — зовётся каждый кадр
        /// ожидания: очередь загрузок показывает это игроку</param>
        /// <returns><c>false</c>, если ждать перестали по отмене.</returns>
        public static async Task<bool> AwaitAsync(
            UnityWebRequest req, UnityWebRequestAsyncOperation op, CancellationToken ct,
            int stallSeconds = 0, Action<UnityWebRequest> onProgress = null)
        {
            if (req == null || op == null) return false;
            double patience = stallSeconds > 0 ? stallSeconds : LvnNetPatience.StallSeconds;
            ulong seen = 0;
            var stall = System.Diagnostics.Stopwatch.StartNew();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { req.Abort(); return false; }
                // Передача жива, пока счётчик байтов движется; мёртв только
                // ЗАМЕРШИЙ счётчик — потому срок и считается от него.
                if (req.downloadedBytes != seen) { seen = req.downloadedBytes; stall.Restart(); }
                else if (stall.Elapsed.TotalSeconds > patience) { req.Abort(); break; }
                onProgress?.Invoke(req);
                await Task.Yield();
            }
            return true;
        }

        /// <summary>
        /// Ждать БЕЗ ОПРОСА — пробуждение по событию завершения.
        ///
        /// <para>Второй способ существует не для красоты. Ожидание циклом
        /// просыпается каждый кадр: это нужно тому, кто СЧИТАЕТ байты (очередь
        /// загрузок показывает прогресс) и кто следит за молчанием передачи. Для
        /// короткого запроса, у которого нет ни прогресса, ни тела в мегабайты
        /// (проверка живости, локальный звук из кэша), опрос — просто трата
        /// кадров: там ждут события.</para>
        ///
        /// <para>Отмена обрывает запрос и всплывает исключением: этот способ
        /// зовут там, где отмена — уже решение вызывающего.</para>
        /// </summary>
        public static async Task CompletedAsync(
            UnityWebRequest req, UnityWebRequestAsyncOperation op, CancellationToken ct)
        {
            if (req == null || op == null) return;
            if (!op.isDone)
            {
                var tcs = new TaskCompletionSource<bool>();
                op.completed += _ => tcs.TrySetResult(true);
                using (ct.CanBeCanceled
                           ? ct.Register(() => { LvnQuiet.Try(req.Abort); })
                           : default)
                    await tcs.Task;
            }
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>Ответ пришёл, но это не ответ: сеть или разбор тела. Проверка
        /// стояла шестью копиями и в двух из них — без <c>DataProcessingError</c>,
        /// то есть битое тело считалось успехом.</summary>
        public static bool Failed(UnityWebRequest req)
            => req == null
               || req.result is UnityWebRequest.Result.ConnectionError
                             or UnityWebRequest.Result.DataProcessingError;
    }
}
