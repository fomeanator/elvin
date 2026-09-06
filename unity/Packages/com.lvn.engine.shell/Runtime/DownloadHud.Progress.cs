using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>Снимок загрузчика становится состоянием карточки. Замеры
    /// относятся к одному пакету, а не к максимуму байтов за жизнь приложения.</summary>
    public sealed partial class DownloadHud
    {
        private bool _shown, _wasWorking, _wasQueued, _queueFinished, _lastOffline;
        private float _quietSince = -1f, _lastAt = -1f, _lastProgressAt;
        private float _speed, _sampleStarted;
        private long _lastBytes;
        private int _lastEpoch, _lastDone, _lastPending;
        private bool _centerDirty;
        private DownloadCenter _watched;

        /// <summary>Обновляет также открытый ИДЛ: конец работы — новое
        /// состояние, а не повод оставить на экране последние мегабайты.</summary>
        public void Tick(TransferSnapshot t)
        {
            float now = Lvn.LvnClock.Wall();
            WatchCenter();
            bool off = Offline?.Invoke() ?? false;
            int pend = PendingOps?.Invoke() ?? 0;
            bool queued = Center != null && (Center.Running || Center.Queue.Count > 0);
            bool work = t.Working || queued;
            bool failed = Center != null && Center.Failed.Count > 0;
            bool visible = work || pend > 0 || failed;
            bool changed = work != _wasWorking || off != _lastOffline || pend != _lastPending;
            _queueFinished = !work && Center != null && Center.LastRunCompleted
                && (_wasQueued || _centerDirty || _queueFinished);

            // Эпоха ловит новый пакет даже без промежуточного пустого тика.
            // Уменьшение байтов ловит повтор/перезапуск одиночного запроса.
            bool reset = _lastAt < 0f || t.Epoch != _lastEpoch || t.Received < _lastBytes
                || t.BatchDone < _lastDone || (work && !_wasWorking);
            if (reset)
            {
                _speed = 0f;
                _lastProgressAt = _sampleStarted = now;
            }
            else
            {
                float dt = now - _lastAt;
                if (dt > 0.05f)
                {
                    float instant = (t.Received - _lastBytes) / dt;
                    _speed = _speed <= 0f ? instant : Mathf.Lerp(_speed, instant, 0.35f);
                }
                if (t.Received > _lastBytes || t.BatchDone > _lastDone) _lastProgressAt = now;
            }
            _lastAt = now;
            _lastEpoch = t.Epoch;
            _lastBytes = t.Received;
            _lastDone = t.BatchDone;

            var phase = DownloadTally.PhaseOf(work, off, pend, now - _lastProgressAt);
            // Оценку уже превысили — остаток неизвестен, а не равен нулю.
            long plan = work && t.Received >= t.PlannedBytes ? 0 : t.PlannedBytes;
            var tally = new DownloadTally(t.Received, plan,
                t.BatchDone, t.BatchTotal, _speed, phase);
            bool moving = phase == DownloadTally.Phase.Running;
            _miniRing.Glyph = off || failed ? RingGlyph.Alert
                : work ? RingGlyph.Down : RingGlyph.Up;
            _miniRing.Progress = work ? tally.Fraction : 0f;
            _bar.style.display = work && tally.PlanKnown ? DisplayStyle.Flex : DisplayStyle.None;
            _barFill.style.width = Length.Percent(Mathf.Clamp01(tally.Fraction) * 100f);
            _info.style.display = work ? DisplayStyle.Flex : DisplayStyle.None;

            var entry = CurrentEntry(t);
            string category = Humanize(ActiveUrl?.Invoke(), LvnWords.Of("downloads.content", "Downloading content"));
            _file.text = entry?.Label ?? category;
            // Область показателей названа словами: это не вся библиотека и
            // не сумма оценок очереди плюс байты чужого активного прогрева.
            _kind.text = LvnWords.Of("dl.current_transfer", "Current download")
                + (entry != null ? ": " + category : "");
            if (!work)
            {
                _file.text = failed ? LvnWords.Of("dl.failed", "Download incomplete")
                    : _queueFinished
                        ? LvnWords.Of("dl.finished", "Download complete")
                        : LvnWords.Of("dl.idle", "No active downloads");
                _kind.text = failed
                    ? LvnWords.Of("dl.retry_hint", "Some files were not saved. Retry the downloads below.")
                    : _queueFinished
                        ? LvnWords.Of("dl.saved", "The selected files are saved on this device.") : "";
            }
            if (phase == DownloadTally.Phase.Offline)
            {
                _file.text = LvnOfflineText.Title;
                _kind.text = pend > 0 && !work
                    ? LvnWords.Of("dl.sync_offline", "Progress is saved on this device. Sync will resume when connected.")
                    : LvnWords.Of("dl.offline_wait", "No connection. Downloads that fail can be retried below.");
            }
            else if (phase == DownloadTally.Phase.Syncing)
            {
                _file.text = LvnWords.Of("downloads.syncing", "Syncing");
                _kind.text = LvnWords.Of("dl.sync_saved", "Progress is saved on this device. Syncing with the server.");
                if (FlushPending != null && now - _lastFlushKick > 5f)
                {
                    _lastFlushKick = now;
                    Lvn.LvnAsync.Fire(FlushPending(), "FlushPending");
                }
            }
            else if (phase == DownloadTally.Phase.Stalled)
            {
                // Отсутствие байтов не доказывает отсутствие сети: возможны
                // ожидание полосы, работа с диском или подготовка ресурсов.
                _kind.text = t.Retrying > 0
                    ? LvnWords.Of("dl.retry_active", "Retrying the download…")
                    : LvnWords.Of("dl.waiting_data", "Waiting for data…");
            }

            bool etaReady = moving && now - _sampleStarted >= 2f && tally.EtaSeconds >= 1f;
            _eta.style.display = etaReady ? DisplayStyle.Flex : DisplayStyle.None;
            _eta.text = etaReady
                ? LvnWords.Of("dl.eta", "≈{0} left", Lvn.UI.LvnTimeWords.Coarse((long)tally.EtaSeconds)) : "";
            ScreenUi.SetText(_vSpeed, moving && _speed >= 1024f ? Speed(_speed) : "—");
            ScreenUi.SetText(_vGot, tally.PlanKnown
                ? Mb(tally.DoneBytes) + " " + LvnWords.Of("common.of", "of") + " " + LvnBytes.Approx(tally.PlanBytes)
                : Mb(tally.DoneBytes));
            ScreenUi.SetText(_vLeft, tally.PlanKnown ? LvnBytes.Approx(tally.LeftBytes) : "—");
            int next = Center == null ? 0 : Center.Queue.Count - (entry != null ? 1 : 0);
            ScreenUi.SetText(_vQueue, next > 0 ? next.ToString() : "—");

            if (_expanded && (_centerDirty || changed))
            {
                Lvn.UI.LvnScroll.Keeping(_sections, () => RebuildSections());
            }
            _centerDirty = false;
            _wasWorking = work;
            _wasQueued = queued;
            _lastOffline = off;
            _lastPending = pend;
            if (visible)
            {
                _quietSince = -1f;
                if (!_shown)
                {
                    _shown = true;
                    style.display = DisplayStyle.Flex;
                    _capsule.experimental.animation.Start(0f, 1f, LvnMotion.Ms(LvnMotion.Normal),
                        (_, p) => _capsule.style.opacity = p);
                }
            }
            else if (_shown && !_expanded)
            {
                if (_quietSince < 0f) _quietSince = now;
                if (now - _quietSince > 2f)
                {
                    _shown = false;
                    _capsule.experimental.animation.Start(1f, 0f, LvnMotion.Ms(200), (_, p) =>
                    {
                        if (_shown) return; // новая работа могла прийти во время гашения
                        _capsule.style.opacity = p;
                        if (p <= 0.01f) style.display = DisplayStyle.None;
                    });
                }
            }
        }

        private DownloadCenter.Entry CurrentEntry(TransferSnapshot snapshot)
        {
            if (Center == null || snapshot.BatchItems == null) return null;
            foreach (var entry in Center.Queue)
                if (entry.Active && ReferenceEquals(entry.Items, snapshot.BatchItems)) return entry;
            return null;
        }

        private void WatchCenter()
        {
            if (_watched == Center) return;
            if (_watched != null) _watched.Changed -= MarkCenterDirty;
            _watched = Center;
            if (_watched != null) _watched.Changed += MarkCenterDirty;
            _centerDirty = true;
        }

        private void MarkCenterDirty() => _centerDirty = true;
    }
}
