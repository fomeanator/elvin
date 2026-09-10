using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЦЕНТР ЗАГРУЗОК (решение Ильи 25.08): «скачать игру» — не один плоский
    /// батч, а ОЧЕРЕДЬ ПО ГЛАВАМ с человеческими именами: видно, что качается,
    /// что ждёт, и любую главу можно снять крестиком (активную — отменой её
    /// токена, очередь просто едет дальше). Единственный владелец
    /// последовательности: батчи идут строго по одному, чтобы не душить сеть
    /// и чтобы прогресс в индикаторе значил ровно одну главу.
    /// </summary>
    public sealed class DownloadCenter
    {
        public sealed class Entry
        {
            public string Label;            // «Cold — глава 3»
            public string Group;            // «Cold» — по нему очередь сводится в новеллы
            public long Bytes;              // оценка недостающего
            public List<PreloadItem> Items;
            public bool Active;
            public int MissingFiles;
        }

        private readonly Func<IReadOnlyList<PreloadItem>, CancellationToken, Task> _preload;
        private readonly Func<string, bool> _isCached;
        private readonly List<Entry> _queue = new List<Entry>();
        private readonly List<Entry> _failed = new List<Entry>();
        private CancellationTokenSource _entryCts;
        private bool _running;
        private long _doneBytes, _totalBytes; // общий прогресс всей очереди

        /// <summary>Очередь изменилась (добавили/сняли/поехала следующая).</summary>
        public event Action Changed;

        public IReadOnlyList<Entry> Queue => _queue;
        public bool Running => _running;
        public IReadOnlyList<Entry> Failed => _failed;
        public bool LastRunCompleted { get; private set; }
        private bool _cancelledInRun;

        /// <summary>Суммарный прогресс ОЧЕРЕДИ (решение Ильи 26.08: «шкалы
        /// общего прогресса нет — надо суммировать»): байты завершённых глав /
        /// сумма всех поставленных. Это оценки объёмов глав, а не показание
        /// сети: HUD не складывает их с байтами текущего пакета.</summary>
        public (long doneBytes, long totalBytes) Progress => (_doneBytes, _totalBytes);

        public DownloadCenter(ContentLoader loader) : this(loader.StartPreloadBatch, loader.IsAssetCached) { }

        internal DownloadCenter(Func<IReadOnlyList<PreloadItem>, CancellationToken, Task> preload,
            Func<string, bool> isCached)
        {
            _preload = preload;
            _isCached = isCached;
        }

        /// <summary>Поставить главу в хвост очереди; пустые списки не занимают
        /// место. Запускает прокачку, если она не шла.</summary>
        public void Enqueue(string label, long bytes, List<PreloadItem> items, string group = null)
        {
            if (items == null || items.Count == 0) return;
            LastRunCompleted = false;
            _queue.Add(new Entry { Label = label, Bytes = bytes, Items = items, Group = group });
            _totalBytes += bytes;
            Changed?.Invoke();
            if (!_running) LvnAsync.Fire(RunAsync(), "Run");
        }

        /// <summary>Снять главу: активная отменяется своим токеном (очередь
        /// сама поедет дальше), ждущая просто выбывает.</summary>
        public void Remove(Entry e)
        {
            if (e == null) return;
            if (_failed.Remove(e)) { Changed?.Invoke(); return; }
            // НАРОЧНО только гасим: источником владеет RunAsync — он снимет,
            // вычтет и освободит сам.
            if (e.Active) _entryCts?.Cancel();
            else if (_queue.Remove(e))
            {
                _totalBytes -= e.Bytes;
                Changed?.Invoke();
            }
        }

        /// <summary>Повторяется только недокачанное. Ошибка остаётся видимой,
        /// пока игрок не повторит загрузку или явно не снимет запись.</summary>
        public void Retry(Entry e)
        {
            if (e == null || !_failed.Remove(e)) return;
            var missing = new List<PreloadItem>();
            long bytes = 0;
            foreach (var item in e.Items)
            {
                if (_isCached(item.Url)) continue;
                missing.Add(item);
                bytes += item.Size > 0 ? item.Size : DownloadPolicy.UnknownSizeBytes;
            }
            Changed?.Invoke();
            Enqueue(e.Label, bytes, missing);
        }

        /// <summary>Ждать, пока очередь не опустеет (для «Скачать всё» из
        /// настроек — их строка живёт до конца полной прокачки).</summary>
        public async Task WhenDrainedAsync()
        {
            while (_running || _queue.Count > 0) await Task.Delay(300);
        }

        private async Task RunAsync()
        {
            _running = true;
            _cancelledInRun = false;
            try
            {
                while (_queue.Count > 0)
                {
                    var e = _queue[0];
                    e.Active = true;
                    _entryCts = new CancellationTokenSource();
                    Changed?.Invoke();
                    bool cancelled = false;
                    bool failed = false;
                    try { await _preload(e.Items, _entryCts.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch (Exception ex)
                    {
                        failed = true;
                        Debug.LogWarning($"[lvn-dl-center] {e.Label}: {ex.Message}");
                    }
                    // BatchDone означает «обработано», а не «сохранено»:
                    // пакет терпит ошибки отдельных файлов. Проверяем итог.
                    e.MissingFiles = 0;
                    if (!cancelled)
                        foreach (var item in e.Items)
                            if (!string.IsNullOrEmpty(item.Url) && !_isCached(item.Url)) e.MissingFiles++;
                    failed |= e.MissingFiles > 0;
                    _entryCts.Dispose();
                    _entryCts = null;
                    _queue.Remove(e);
                    e.Active = false;
                    _cancelledInRun |= cancelled;
                    if (failed && !cancelled) _failed.Add(e);
                    if (cancelled || failed) _totalBytes -= e.Bytes;
                    else _doneBytes += e.Bytes;
                    Changed?.Invoke();
                }
            }
            finally
            {
                _running = false;
                if (_queue.Count == 0) { _doneBytes = 0; _totalBytes = 0; }
                LastRunCompleted = !_cancelledInRun && _failed.Count == 0;
                Changed?.Invoke();
            }
        }
    }
}
