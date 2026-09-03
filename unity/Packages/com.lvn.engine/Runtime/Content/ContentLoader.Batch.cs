using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// ОБОЗ — пакетная загрузка вперёд: главы, обложки, набор для входа.
    ///
    /// <para>Разница с одиночной загрузкой не в объёме, а в обещании: обоз
    /// говорит, СКОЛЬКО осталось, и потому за ним можно показывать полосу и
    /// им можно управлять — приостановить, отменить, дождаться именно этих
    /// адресов. Одиночная загрузка обещает только себя.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        public Task Prefetch(string url, string kind, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return Task.CompletedTask;
            return kind switch
            {
                LvnParts.Script => DownloadScriptText(url, ct),
                _ => DownloadAssetBytes(url, ct),
            };
        }

        /// <summary>Downloads a list of assets, pipelining disk writes with the
        /// next file's network setup so the progress bar shows smooth overall
        /// progress and there's no idle gap between files. Files already on disk
        /// are skipped (they don't inflate the total). Returns a Task the caller
        /// can await; <see cref="WaitForAll"/>(null) also works.</summary>
        public Task StartPreloadBatch(IReadOnlyList<PreloadItem> assets, CancellationToken ct)
        {
            if (assets == null || assets.Count == 0) return Task.CompletedTask;

            // Register all aliases up-front so the HUD label is ready the moment a
            // fetch starts (no one-frame flash of raw URL).
            foreach (var a in assets)
                if (!string.IsNullOrEmpty(a.Alias) && !string.IsNullOrEmpty(a.Url))
                    lock (_aliases) _aliases[a.Url] = a.Alias;

            // Count how many files are actually missing from disk cache.
            var pending = new List<PreloadItem>(assets.Count);
            foreach (var a in assets)
            {
                if (string.IsNullOrEmpty(a.Url)) continue;
                var path = CachePath(_assetCacheDir, a.Url, ".bin");
                if (!File.Exists(path)) pending.Add(a);
            }
            if (pending.Count == 0) return Task.CompletedTask;

            // ОЧЕРЕДЬ, А НЕ ПОДМЕНА.
            //
            // Ключ был ОДИН на все пакеты («__preload_batch__»), и второй
            // вызывающий получал ЧУЖУЮ задачу вместо своей. А вызывающих
            // четверо: бут греет обложки, менеджер тянет следующую главу,
            // центр загрузок ведёт очередь глав, кнопка «Докачать» в
            // настройках просит остаток. Стоило нажать «Докачать», пока идёт
            // очередь, — и её глава «скачивалась» чужой задачей: центр снимал
            // запись с очереди, файлов никто не качал, а счётчики показывали
            // «глав 1 · файлов 0» при скорости «—» и остаток, который не
            // уменьшался (живой скрин 01.09).
            //
            // Тот же СПИСОК по-прежнему схлопывается в одну задачу (повторный
            // запрос той же главы не качает её дважды), а РАЗНЫЕ списки честно
            // встают в очередь: полоса пропускания одна, и параллелить их
            // нечем.
            string batchKey = "__preload_batch__" + BatchKey(pending);
            Task<byte[]> batchTask;
            lock (_underway)
            {
                if (_underway.TryGetValue(batchKey, out var same) && same.Work is Task<byte[]> running)
                    return running;
                batchTask = RunBatchQueuedAsync(pending, ct);
                var rec = Progress(batchKey);
                rec.Work = batchTask;
                rec.Bundle = true;   // это весь пакет, а не файл в полёте
            }
            _ = batchTask.ContinueWith(_ =>
            {
                lock (_underway) _underway.Remove(batchKey);
            }, TaskScheduler.Default);
            return batchTask;
        }

        /// <summary>
        /// ОЧЕРЕДЬ ОПУСТЕЛА — счёт обнуляется ВЕСЬ.
        ///
        /// <para>Счёт складывают шесть полей: сколько всего, сколько сделано,
        /// что скачивалось последним, попытки и два словаря байтов. Забыть одно
        /// из них — значит показать игроку «Скачано 131 из 135» при пустой
        /// очереди (живой скрин): мусор одиночных фетчей въезжает в проценты
        /// следующего пакета.</para>
        ///
        /// <para>Обнуление стояло дважды — в конце пакета и в конце одиночной
        /// загрузки, — и это ровно то место, где поле теряют.</para>
        ///
        /// <para>Звать ТОЛЬКО под <c>lock (_underway)</c>: те же поля читает
        /// полоса загрузки из другого потока.</para>
        /// </summary>
        private void ClearBatchTally()
        {
            BatchTotal     = 0;
            BatchDone      = 0;
            LastStartedUrl = null;
            // Итоги пакета сброшены — записям о загрузках сказать больше
            // нечего. Но ИДУЩУЮ работу сброс итогов не отменяет: раньше здесь
            // стоял Clear целиком, и с переездом задачи в запись он снёс бы
            // пакет главы — а с ним и защиту от повторного скачивания той же
            // главы вторым запросом.
            var idle = new List<string>();
            foreach (var kv in _underway)
            {
                kv.Value.Received = 0; kv.Value.Expected = 0; kv.Value.Attempt = 0;
                if (kv.Value.Work == null) idle.Add(kv.Key);
            }
            foreach (var k in idle) _underway.Remove(k);
        }

        // Пакеты идут ПО ОЧЕРЕДИ: канал один, и два пакета, тянущие его
        // одновременно, только делят его пополам — зато оба показывают половину
        // скорости и вдвое больше ждут.
        private readonly System.Threading.SemaphoreSlim _batchGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Устойчивое имя пакета — по списку адресов. Тот же список
        /// (повторный запрос главы) находит свою задачу, чужой не находит.</summary>
        private static string BatchKey(List<PreloadItem> pending)
        {
            unchecked
            {
                int h = 17;
                foreach (var a in pending) h = h * 31 + (a.Url?.GetHashCode() ?? 0);
                return pending.Count + ":" + h;
            }
        }

        private async Task<byte[]> RunBatchQueuedAsync(List<PreloadItem> pending, CancellationToken ct)
        {
            // ПАКЕТ ЖИВЫМ НЕ БЫВАЕТ. Молчание тут читалось бы как «на это
            // смотрят»: пачка заняла бы бронь и не смогла уступить. Если
            // звонящий ступень назвал (центр загрузок говорит «библиотека»),
            // его слово сильнее — перебивать нельзя.
            using var rung = LvnRungScope.AtLeast(LvnRung.CurrentChapter);
            await _batchGate.WaitAsync(ct);
            try
            {
                lock (_underway)
                {
                    // Чистый старт: словари байтов копят и одиночные фетчи
                    // (фоновый стриминг), и их мусор въезжал в прогресс батча —
                    // «Скачано 131 из 135» при пустой очереди (живой скрин).
                    // ЧИСТЫЙ СТАРТ ПО БАЙТАМ, но не по повторам: счётчик
                    // попыток принадлежит идущей закачке, и обнулить его здесь
                    // значило бы подарить ей лишний повтор.
                    foreach (var f in _underway.Values) { f.Received = 0; f.Expected = 0; }
                    BatchTotal     = pending.Count;
                    BatchDone      = 0;
                    LastStartedUrl = pending[0].Url;
                }
                return await RunBatchAsync(pending, ct);
            }
            finally
            {
                lock (_underway) ClearBatchTally();
                _batchGate.Release();
            }
        }

        /// <summary>
        /// ОБОЗ ИДЁТ В НЕСКОЛЬКО ПОЛОС.
        ///
        /// <para>Раньше пакет качался СТРОГО ПО ОДНОМУ файлу, а простой между
        /// файлами закрывали тёплым стартом следующего на 90% текущего. Это
        /// лечило симптом: полоса сети шириной двенадцать (<see cref="LvnLanes.Wire"/>)
        /// всё это время держала одиннадцать мест пустыми. Набор первого кадра —
        /// это десятки МЕЛКИХ файлов (рамка реплики, значки, полотно витрины), и
        /// их цена — не байты, а круговой рейс на каждый: на мобильной сети
        /// тридцать файлов по одному это тридцать задержек подряд.</para>
        ///
        /// <para>Теперь по списку идут несколько рабочих сразу, а сколько их
        /// реально поедет, решает та же полоса — воркер сверх её ширины просто
        /// стоит на входе и ничего не занимает. Бронь для живого (два места)
        /// продолжает действовать: пакет входит по ступени, и открытая глава
        /// его подвинет.</para>
        ///
        /// <para>Тёплый старт следующего файла УБРАН вместе с очередью: он
        /// закрывал разрыв, которого больше нет, а его состояние (какой адрес
        /// уже греется) при нескольких рабочих было бы общим и врало.</para>
        /// </summary>
        private async Task<byte[]> RunBatchAsync(List<PreloadItem> pending, CancellationToken ct)
        {
            // Общий курсор по списку: каждый рабочий берёт следующий свободный.
            int cursor = -1;
            int workers = BatchWorkerCount(pending.Count);

            var crew = new Task[workers];
            for (int w = 0; w < workers; w++) crew[w] = WorkerAsync();
            await Task.WhenAll(crew);

            CurrentFileLabel = null;
            return null;

            async Task WorkerAsync()
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int i = Interlocked.Increment(ref cursor);
                    if (i >= pending.Count) return;

                    var asset = pending[i];
                    var path  = CachePath(_assetCacheDir, asset.Url, ".bin");
                    // Успел приехать одиночным запросом, пока пакет шёл, — это
                    // не работа, но в счёте она посчитана.
                    if (File.Exists(path)) { lock (_underway) BatchDone++; continue; }

                    // Подпись показывает ПОСЛЕДНИЙ начатый файл. При нескольких
                    // рабочих «текущий» — понятие приблизительное, и честнее
                    // назвать тот, что тронулся только что, чем молчать.
                    CurrentFileLabel = AliasOf(asset.Url);
                    LastStartedUrl   = asset.Url;

                    // ОДИН ЗАХОД ЗА ФАЙЛОМ НА ВЕСЬ ЗАГРУЗЧИК.
                    //
                    // Пакет ходил своим путём (FetchToMemory + запись на диск)
                    // и потому не умел трёх вещей, которые общий заход умеет
                    // давно: СИДА (файл лежит в APK — а пакет всё равно шёл в
                    // сеть, и весь смысл сида для набора первого кадра
                    // пропадал), ДОКАЧКИ С БАЙТА N (оборванный на 90% фон
                    // качался с нуля) и ответа 416 (кусок уже полон — не
                    // хватало переименования).
                    //
                    // Своя копия правил дала бы четвёртое место, где их надо
                    // держать в согласии. Их и так три.
                    try { await DownloadAssetBytes(asset.Url, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* сдался и объяснил внутри: NoteGaveUp / RememberMissing */ }

                    lock (_underway) BatchDone++;
                }
            }
        }

        /// <summary>Сколько рабочих ведут пакет.
        ///
        /// <para>Ширину даёт ПОЛОСА СЕТИ, а не своё число: два места в ней
        /// оставлены живому запросу (открытая глава просит картинку, которую
        /// игрок уже видит пустой), и занимать их пакетом нельзя даже когда
        /// больше некому. Рабочих сверх работы тоже не бывает: на трёх файлах
        /// десять рабочих — это девять пустых проходов по курсору.</para>
        ///
        /// <para>Один — нижняя граница: пакет из одного файла всё равно должен
        /// поехать.</para>
        /// </summary>
        internal static int BatchWorkerCount(int pending)
        {
            int width = Math.Max(1, LvnLanes.Wire.Width - LvnLanes.Wire.KeptForLive);
            return pending < width ? Math.Max(1, pending) : width;
        }


        // Returns the URL of the first file in pending[fromIdx..] not yet on disk.
        private string FindNextUncachedUrl(List<PreloadItem> pending, int fromIdx)
        {
            for (int j = fromIdx; j < pending.Count; j++)
                if (!File.Exists(CachePath(_assetCacheDir, pending[j].Url, ".bin")))
                    return pending[j].Url;
            return null;
        }



        /// <summary>Waits until either the listed urls finish prefetching, or (if
        /// <paramref name="urls"/> is null) until the whole batch settles — no
        /// task in flight and the counters reset. Polling BatchActive catches
        /// tasks that join the queue mid-wait.</summary>
        public async Task WaitForAll(IEnumerable<string> urls, CancellationToken ct = default)
        {
            if (urls == null)
            {
                while (BatchActive)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await Task.Delay(50, ct); }
                    catch (OperationCanceledException) { throw; }
                }
                return;
            }
            List<Task> tasks;
            lock (_underway)
            {
                tasks = new List<Task>();
                foreach (var u in urls)
                    if (_underway.TryGetValue(u, out var f) && f.Work != null) tasks.Add(f.Work);
            }
            if (tasks.Count == 0) return;
            try { await Task.WhenAll(tasks).WithCancellation(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* individual asset failures don't block the wait */ }
        }
    }
}
