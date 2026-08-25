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
            public long Bytes;              // оценка недостающего
            public List<PreloadItem> Items;
            public bool Active;
        }

        private readonly ContentLoader _loader;
        private readonly List<Entry> _queue = new List<Entry>();
        private CancellationTokenSource _entryCts;
        private bool _running;
        private long _doneBytes, _totalBytes; // общий прогресс всей очереди

        /// <summary>Очередь изменилась (добавили/сняли/поехала следующая).</summary>
        public event Action Changed;

        public IReadOnlyList<Entry> Queue => _queue;
        public bool Running => _running;

        /// <summary>Суммарный прогресс ОЧЕРЕДИ (решение Ильи 26.08: «шкалы
        /// общего прогресса нет — надо суммировать»): байты завершённых глав /
        /// сумма всех поставленных. Байты активного батча добавляет индикатор
        /// (лоадер чистит их концом главы — сложение честное).</summary>
        public (long doneBytes, long totalBytes) Progress => (_doneBytes, _totalBytes);

        public DownloadCenter(ContentLoader loader) { _loader = loader; }

        /// <summary>Поставить главу в хвост очереди; пустые списки не занимают
        /// место. Запускает прокачку, если она не шла.</summary>
        public void Enqueue(string label, long bytes, List<PreloadItem> items)
        {
            if (items == null || items.Count == 0) return;
            _queue.Add(new Entry { Label = label, Bytes = bytes, Items = items });
            _totalBytes += bytes;
            Changed?.Invoke();
            if (!_running) _ = RunAsync();
        }

        /// <summary>Снять главу: активная отменяется своим токеном (очередь
        /// сама поедет дальше), ждущая просто выбывает.</summary>
        public void Remove(Entry e)
        {
            if (e == null) return;
            if (e.Active) _entryCts?.Cancel(); // RunAsync снимет и вычтет сам
            else if (_queue.Remove(e))
            {
                _totalBytes -= e.Bytes;
                Changed?.Invoke();
            }
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
            try
            {
                while (_queue.Count > 0)
                {
                    var e = _queue[0];
                    e.Active = true;
                    Changed?.Invoke();
                    _entryCts = new CancellationTokenSource();
                    bool cancelled = false;
                    try { await _loader.StartPreloadBatch(e.Items, _entryCts.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch (Exception ex) { Debug.LogWarning($"[dl-center] {e.Label}: {ex.Message}"); }
                    _entryCts.Dispose();
                    _entryCts = null;
                    _queue.Remove(e);
                    if (cancelled) _totalBytes -= e.Bytes; // снятая — вон из знаменателя
                    else _doneBytes += e.Bytes;
                    Changed?.Invoke();
                }
            }
            finally
            {
                _running = false;
                if (_queue.Count == 0) { _doneBytes = 0; _totalBytes = 0; }
            }
        }
    }
}
