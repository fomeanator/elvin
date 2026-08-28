using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// ОЧЕРЕДЬ НА ОТПРАВКУ — накопить, пережить перезапуск, отослать пачкой и
    /// правильно понять ответ сервера.
    ///
    /// <para>Устройство было записано дважды — у аналитики и у диагностики, — и
    /// слово в слово: список в памяти, копия в записной книжке, порог «пора
    /// слать», таймер, отправка на паузе и выходе, обрезка старейшего сверх
    /// предела. Отличались только числа, и это законно: диагностика шлётся
    /// чаще и хранится набело, аналитика — реже и карандашом.</para>
    ///
    /// <para>Незаконным было другое: ОБЕ считали успехом ровно код 200, а любой
    /// иной ответ — поводом повторить ту же пачку. Ответ 400 — «эта пачка
    /// неисправна» (устаревшая схема, слишком большое тело, кривое поле): её
    /// повтор не починит, а очередь она держит собой, и за ней не уезжает
    /// НИЧЕГО. До переполнения предела (500 событий, 300 строк) не доходит
    /// вообще ничего, и всё это время лог и отчёт молчат — при живом сервере
    /// и живой сети.</para>
    ///
    /// <para>Поэтому правило ответа теперь одно и записано здесь: 2xx —
    /// доставлено; 4xx (кроме «подожди» и «слишком часто») — пачка неисправима,
    /// её выбрасывают и жалуются в лог, чтобы за ней поехало остальное; всё
    /// прочее (5xx, обрыв, оффлайн) — держим и пробуем позже.</para>
    ///
    /// <para>Насос тоже общий: раньше каждая очередь заводила свой
    /// <c>MonoBehaviour</c> со своим <c>Update</c>. Один объект будит все
    /// ящики — и, что важнее, ОДИНАКОВО отправляет их при уходе в фон и на
    /// выходе, а это тот момент, ради которого очередь и ведётся.</para>
    /// </summary>
    public sealed class LvnOutbox
    {
        private static readonly List<LvnOutbox> _all = new List<LvnOutbox>();

        private readonly string _name;
        private readonly string _key;
        private readonly int _cap;
        private readonly int _flushAt;
        private readonly float _everySec;
        private readonly bool _durable;
        private readonly int _batchMax;
        private readonly Func<JArray, Task<long>> _send;

        private readonly List<JObject> _queue = new List<JObject>();
        private bool _loaded, _flushing, _dirty;
        private float _lastFlush;

        /// <param name="name">имя для журнала</param>
        /// <param name="key">ключ записной книжки</param>
        /// <param name="cap">предел очереди; сверх него уходит СТАРЕЙШЕЕ —
        /// свежее ближе к тому, что игрок делает прямо сейчас</param>
        /// <param name="flushAt">сколько накопить, чтобы слать не дожидаясь таймера</param>
        /// <param name="everySec">как часто будить отправку</param>
        /// <param name="durable">набело (переживает снятие процесса) или
        /// карандашом (дешевле, теряется разве что хвост)</param>
        /// <param name="batchMax">сколько уходит за один раз</param>
        /// <param name="send">отправка пачки; возвращает код ответа, 0 — сеть</param>
        public LvnOutbox(string name, string key, int cap, int flushAt, float everySec,
                         bool durable, int batchMax, Func<JArray, Task<long>> send)
        {
            _name = name; _key = key; _cap = cap; _flushAt = flushAt;
            _everySec = everySec; _durable = durable; _batchMax = batchMax; _send = send;
            lock (_all) _all.Add(this);
        }

        /// <summary>Сколько ждёт отправки. Грузит сохранённое: без этого
        /// свежесозданный ящик отвечал бы «пусто» на очередь, которая на диске
        /// есть, — и спрашивающий (диагностика, насос) верил бы ему.</summary>
        public int Count { get { Load(); lock (_queue) return _queue.Count; } }

        /// <summary>Поработать с очередью под замком: дедупликация повторов и
        /// хвост для отзыва — правила ВЛАДЕЛЬЦА, а не ящика.</summary>
        public void Modify(Action<List<JObject>> act)
        {
            if (act == null) return;
            Load();
            lock (_queue) act(_queue);
        }

        /// <summary>Положить в очередь. <paramref name="persistNow"/> — записать
        /// сразу (след падения обязан пережить падение); иначе запись отложится
        /// до ближайшего тика насоса.</summary>
        public void Add(JObject item, bool persistNow = true, bool mainThread = true)
        {
            if (item == null) return;
            Load();
            lock (_queue)
            {
                _queue.Add(item);
                while (_queue.Count > _cap) _queue.RemoveAt(0);
            }
            Runner.Ensure();
            if (persistNow && mainThread) Persist(); else _dirty = true;
            if (mainThread && Count >= _flushAt) Lvn.LvnAsync.Fire(FlushAsync(), "Flush:" + _name);
        }

        /// <summary>Отправить накопленное. Очередь остаётся, если доставка не
        /// состоялась по причине, которую можно пережить.</summary>
        public async Task FlushAsync()
        {
            if (_flushing || _send == null) return;
            Load();
            JArray batch;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                batch = new JArray(_queue.GetRange(0, Math.Min(_queue.Count, _batchMax)));
            }
            _flushing = true;
            try
            {
                long code = await _send(batch);
                if (LvnBackend.Ok(code))
                {
                    Drop(batch.Count);
                }
                else if (Hopeless(code))
                {
                    // Выбрасываем — иначе неисправимая пачка держит собой всё
                    // остальное, и до сервера не доезжает НИЧЕГО.
                    Debug.LogWarning($"[lvn-outbox] {_name}: сервер отверг пачку из {batch.Count} " +
                                     $"(код {code}) — выброшена, чтобы за ней поехало остальное");
                    Drop(batch.Count);
                }
                // прочее — 5xx, обрыв, оффлайн — держим до следующего раза
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[lvn-outbox] {_name}: отправка сорвалась ({e.Message}) — очередь держим");
            }
            finally { _flushing = false; _lastFlush = Lvn.LvnClock.Wall(); }
        }

        // 408 «подожди» и 429 «слишком часто» — это «позже», а не «неисправимо».
        private static bool Hopeless(long code)
            => code >= 400 && code < 500 && code != 408 && code != 429;

        private void Drop(int n)
        {
            lock (_queue) _queue.RemoveRange(0, Math.Min(_queue.Count, n));
            Persist();
        }

        /// <summary>Записать очередь на устройство. Никогда не бросает: ни
        /// аналитика, ни диагностика не имеют права мешать игре.</summary>
        public void Persist()
        {
            try
            {
                lock (_queue)
                {
                    var json = new JArray(_queue).ToString(Newtonsoft.Json.Formatting.None);
                    if (_durable) LvnKeep.Put(_key, json);
                    else LvnKeep.Jot(_key, json);
                }
            }
            catch { /* не отправилось и не записалось — игре знать незачем */ }
        }

        /// <summary>Поднять сохранённую очередь. Испорченная запись не повод
        /// ронять игру — начинаем с пустой.</summary>
        public void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var raw = LvnKeep.Get(_key, "");
                if (string.IsNullOrEmpty(raw)) return;
                lock (_queue)
                    foreach (var t in JArray.Parse(raw))
                        if (t is JObject o) _queue.Add(o);
            }
            catch { /* испорченная очередь — не повод падать */ }
        }

        /// <summary>Забыть накопленное вместе с записью на устройстве.</summary>
        public void Clear()
        {
            _loaded = true;
            lock (_queue) _queue.Clear();
            try { LvnKeep.Drop(_key); } catch { /* забыли в памяти — записи на диске всё равно не будет: её перезапишет ближайший Persist */ }
        }

        // ── общий насос ──────────────────────────────────────────────────────
        // Один объект на все ящики: раньше каждая очередь заводила свой, и
        // «отправить при уходе в фон» было записано дважды — то есть могло
        // разойтись ровно там, где сходиться важнее всего.
        private sealed class Runner : MonoBehaviour
        {
            private static Runner _inst;

            public static void Ensure()
            {
                if (_inst != null || !Application.isPlaying) return;
                var go = new GameObject("LvnOutbox") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<Runner>();
            }

            private void Update()
            {
                LvnOutbox[] boxes;
                lock (_all) boxes = _all.ToArray();
                foreach (var b in boxes)
                {
                    if (b._dirty) { b._dirty = false; b.Persist(); }
                    if (Lvn.LvnClock.Wall() - b._lastFlush > b._everySec && b.Count > 0)
                        Lvn.LvnAsync.Fire(b.FlushAsync(), "Flush:" + b._name);
                }
            }

            private void OnApplicationPause(bool paused) { if (paused) FlushAll(); }
            private void OnApplicationQuit() => FlushAll();

            private static void FlushAll()
            {
                LvnOutbox[] boxes;
                lock (_all) boxes = _all.ToArray();
                foreach (var b in boxes)
                {
                    b.Persist();                    // сначала на диск: отправка может не успеть
                    Lvn.LvnAsync.Fire(b.FlushAsync(), "Flush:" + b._name);
                }
            }
        }
    }
}
