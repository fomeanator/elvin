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
        private long _lastSent = -1;
        private int _lastEpoch, _lastDone, _lastPending;
        private bool _centerDirty;
        private DownloadCenter _watched;

        /// <summary>Обновляет также открытый ИДЛ: конец работы — новое
        /// состояние, а не повод оставить на экране последние мегабайты.</summary>
        // Пакеты не из очереди, доехавшие рядом с ней: байты и оценки.
        private long _sideDone, _sidePlan, _lastSideBytes, _lastSidePlan;
        private bool _lastWasSide;

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
            bool newEpoch = t.Epoch != _lastEpoch;
            bool reset = _lastAt < 0f || newEpoch || t.Received < _lastBytes
                || t.BatchDone < _lastDone || (work && !_wasWorking);
            // ПАКЕТ СМЕНИЛСЯ, А РАБОТА НЕТ. «Скачать всю игру» ставит главу за
            // главой, прогрев библиотеки идёт ступенями — каждый пакет новая
            // эпоха. Сбрасывать на ней скорость значило показывать «—» на
            // каждой границе, а график — стирать минуту по десять раз. Между
            // пакетами скорость продолжается, а байты нового пакета считаются
            // с его первого снимка.
            bool handover = reset && work && _wasWorking && t.Epoch != _lastEpoch && _lastAt >= 0f;
            float chartDown = 0f;
            if (handover)
            {
                chartDown = t.Received;
                _lastProgressAt = now;
            }
            else if (reset)
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
            // Байты этой секунды — в график: приём из снимка, отдача от служб.
            // ГРАФИК НЕ СБРАСЫВАЕТСЯ НИКОГДА: это скользящая минута, и она
            // чистит себя сама. Сброс по «байт стало меньше» (файл закрылся,
            // повтор запроса) стирал историю по десять раз в секунду — кривая
            // не успевала прожить и одной секунды, лист показывал ровный ноль
            // при живых мегабайтах в подписи. Провал байтов — это ноль за тик,
            // а не новая жизнь.
            long sentNow = Lvn.Services.LvnBackend.BytesSent;
            float upDelta = _lastSent < 0 ? 0f : Mathf.Max(0f, sentNow - _lastSent);
            _lastSent = sentNow;
            float downDelta = handover ? chartDown
                : t.Received > _lastBytes && _lastAt >= 0f ? t.Received - _lastBytes : 0f;
            _chart.Add(now, downDelta, upDelta);
            _lastAt = now;
            _lastEpoch = t.Epoch;
            _lastBytes = t.Received;
            _lastDone = t.BatchDone;

            var phase = DownloadTally.PhaseOf(work, off, pend, now - _lastProgressAt);
            // Оценку уже превысили — остаток неизвестен, а не равен нулю.
            long plan = work && t.Received >= t.PlannedBytes ? 0 : t.PlannedBytes;
            // ВСЯ ОЧЕРЕДЬ, А НЕ ОДИН ПАКЕТ. «Скачать всю игру» ставит главу за
            // главой, и процент по одному пакету прыгал бы к нулю на каждой —
            // игрок видел бы не «сколько осталось игры», а «сколько осталось
            // главы». Пока очередь идёт, доля считается по ней: завершённые
            // главы (их оценки) плюс живые байты текущего пакета — против
            // суммы всех поставленных. Без очереди — прежний пакетный счёт.
            var entry = CurrentEntry(t);
            var queue = Center != null ? Center.Progress : (0L, 0L);
            bool wholeQueue = work && Center != null && Center.Queue.Count > 0 && queue.Item2 > 0;
            long received = t.Received, planned = plan;
            // ЧУЖАЯ РАБОТА РЯДОМ С ОЧЕРЕДЬЮ ТОЖЕ СЧИТАЕТСЯ. Прогрев библиотеки
            // («Персонажи и наряды», «Фоны сцен») идёт тем же загрузчиком и
            // держит очередь в ожидании: десять секунд лист показывал «0 % ·
            // 0 МБ» при живых 2,4 МБ/с в подписи (ролик 09.09). Байты таких
            // пакетов идут И В СЧЁТ, И В ПЛАН: оценки у прогрева нет, а без
            // плана доля перевалила бы за сто раньше конца очереди. Так доля
            // не падает на границе пакетов и доходит до ста ровно с очередью;
            // с концом очереди накопленное забывается.
            bool side = wholeQueue && entry == null && t.Working;
            if (wholeQueue)
            {
                if (_lastWasSide && (newEpoch || !side))
                {
                    _sideDone += _lastSideBytes;
                    _sidePlan += System.Math.Max(_lastSideBytes, _lastSidePlan);
                }
                long inFlight = side ? t.Received : entry != null ? System.Math.Min(t.Received, entry.Bytes) : 0L;
                received = queue.Item1 + _sideDone + inFlight;
                long sidePlanNow = side ? System.Math.Max(t.PlannedBytes, t.Received) : 0L;
                planned = System.Math.Max(queue.Item2 + _sidePlan + sidePlanNow, received);
            }
            else { _sideDone = _sidePlan = 0L; }
            _lastWasSide = side;
            _lastSideBytes = side ? t.Received : 0L;
            _lastSidePlan = side ? t.PlannedBytes : 0L;
            // «Осталось» — по средней за окно, а не по мгновенной: мелкие файлы
            // роняют мгновенную в десять раз, и оценка прыгала минутами.
            float etaSpeed = _chart.AvgDown(Mathf.Clamp(now - _sampleStarted, 2f, 15f));
            var tally = new DownloadTally(received, planned,
                t.BatchDone, t.BatchTotal, etaSpeed > 0f ? etaSpeed : _speed, phase);
            bool moving = phase == DownloadTally.Phase.Running;
            _miniRing.Glyph = off || failed ? RingGlyph.Alert
                : work ? RingGlyph.Down : RingGlyph.Up;
            _miniRing.Progress = work ? tally.Fraction : 0f;
            _bar.style.display = work && tally.PlanKnown ? DisplayStyle.Flex : DisplayStyle.None;
            _barFill.style.width = Length.Percent(Mathf.Clamp01(tally.Fraction) * 100f);
            _info.style.display = work ? DisplayStyle.Flex : DisplayStyle.None;

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
            ScreenUi.SetText(_vUp, _chart.LastUp >= 256f ? Speed(_chart.LastUp) : "—");
            // Процент — только когда план известен: доля без плана это догадка.
            bool showPercent = work && tally.PlanKnown;
            _percent.style.display = showPercent ? DisplayStyle.Flex : DisplayStyle.None;
            if (showPercent) _percent.text = Mathf.FloorToInt(Mathf.Clamp01(tally.Fraction) * 100f) + "%";
            ApplyStateChip(phase, work, failed, _queueFinished);
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

        /// <summary>Чип состояния в шапке листа: одно слово о том, что сейчас
        /// происходит, — идёт, ждёт, нет сети, синк, готово, отказ.</summary>
        private void ApplyStateChip(DownloadTally.Phase phase, bool work, bool failed, bool finished)
        {
            string text; Color tint;
            if (phase == DownloadTally.Phase.Offline) { text = LvnWords.Of("dl.state_offline", "Offline"); tint = LvnTokens.Warn; }
            else if (failed && !work) { text = LvnWords.Of("dl.state_failed", "Incomplete"); tint = LvnTokens.Warn; }
            else if (phase == DownloadTally.Phase.Syncing) { text = LvnWords.Of("dl.state_syncing", "Syncing"); tint = LvnTokens.Gold; }
            else if (phase == DownloadTally.Phase.Stalled) { text = LvnWords.Of("dl.state_waiting", "Waiting"); tint = LvnTokens.TextDim; }
            else if (work) { text = LvnWords.Of("dl.state_running", "Downloading"); tint = LvnTokens.Accent; }
            else if (finished) { text = LvnWords.Of("dl.state_done", "Done"); tint = LvnTokens.Ok; }
            else { text = LvnWords.Of("dl.state_idle", "Idle"); tint = LvnTokens.TextDim; }
            _state.text = text.ToUpperInvariant();
            _state.style.color = tint;
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
