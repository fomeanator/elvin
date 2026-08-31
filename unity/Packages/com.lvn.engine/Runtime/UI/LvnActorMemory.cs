using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn.UI
{
    /// <summary>
    /// ЧТО СЦЕНА ПОМНИТ О ФИГУРЕ — одной записью на человека.
    ///
    /// <para>Помнила она пятью отдельными словарями по одному и тому же ключу:
    /// последняя команда, куда просили встать, кто ставил позу, что надето, где
    /// фигура стоит после перетаскивания. Менять их полагалось ВМЕСТЕ —
    /// показал, записал в три; убрал, вычистил из четырёх, — и правило это
    /// держалось на памяти того, кто правит. Словари при этом разъехались по
    /// девяти файлам сцены.</para>
    ///
    /// <para>Цена ошибки тихая и потому дорогая: забытая запись не роняет
    /// ничего, она делает фигуру НЕМНОГО не той — встаёт по-старому месту,
    /// наследует чужую позу, пересобирается там, где могла бы просто
    /// показаться. Каждый такой случай ищется глазами по всей сцене.</para>
    ///
    /// <para>Здесь эти пять — одна запись, и «забыть фигуру» снова одно
    /// действие. Правила, которые раньше приходилось помнить, стали именами
    /// методов: <see cref="ForgetPoses"/> уносит память ГЛАВЫ и оставляет
    /// облик, потому что облик — свойство самой фигуры, а не договора истории
    /// с собой.</para>
    /// </summary>
    public sealed class LvnActorMemory
    {
        private sealed class Entry
        {
            public JObject Cmd;            // последняя команда актёра
            public Placement Target;       // куда просили встать
            public bool HasTarget;
            public LvnSender Pose;         // кто ставил позу в последний раз
            public bool HasPose;
            public string Look;            // список слоёв, собранных на фигуре
            public Placement Where;        // где стоит на самом деле (перетаскивание)
            public bool HasWhere;
        }

        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>();

        private Entry Of(string id)
        {
            if (!_byId.TryGetValue(id, out var e)) { e = new Entry(); _byId[id] = e; }
            return e;
        }

        // ── последняя команда и её отправитель ──────────────────────────────

        /// <summary>Запомнить команду и того, кто её отдал: повтор показа
        /// пересобирает ТУ ЖЕ позу с новым нарядом, а правило «авторская
        /// команда не наследует позу витрины» смотрит на отправителя.</summary>
        public void Remember(string id, JObject cmd, LvnSender sender)
        {
            if (string.IsNullOrEmpty(id)) return;
            var e = Of(id);
            e.Cmd = cmd;
            e.Pose = sender;
            e.HasPose = true;
        }

        /// <summary>Вернуть команду, НЕ трогая отправителя позы. Примерка
        /// прячет фигуру и возвращает её прежней командой — но ставил-то её
        /// по-прежнему автор, и правило «авторская команда не наследует позу
        /// витрины» смотрит именно на отправителя.</summary>
        public void RestoreCommand(string id, JObject cmd)
        {
            if (string.IsNullOrEmpty(id)) return;
            Of(id).Cmd = cmd;
        }

        public bool TryCommand(string id, out JObject cmd)
        {
            cmd = null;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var e) || e.Cmd == null) return false;
            cmd = e.Cmd;
            return true;
        }

        /// <summary>Помнит ли сцена, чем пересобрать эту фигуру.</summary>
        public bool Knows(string id)
            => !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var e) && e.Cmd != null;

        public bool TryPoseSender(string id, out LvnSender sender)
        {
            sender = default;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var e) || !e.HasPose) return false;
            sender = e.Pose;
            return true;
        }

        // ── куда просили встать ────────────────────────────────────────────
        //
        // Место известно ДО того, как доедет арт: имя говорящего встаёт с
        // нужной стороны с первого кадра, не дожидаясь картинки.

        public void SetTarget(string id, Placement placement)
        {
            if (string.IsNullOrEmpty(id)) return;
            var e = Of(id);
            e.Target = placement;
            e.HasTarget = true;
        }

        public bool TryTarget(string id, out Placement placement)
        {
            placement = default;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var e) || !e.HasTarget) return false;
            placement = e.Target;
            return true;
        }

        // ── где фигура стоит на самом деле ─────────────────────────────────

        public void SetWhere(string id, Placement placement)
        {
            if (string.IsNullOrEmpty(id)) return;
            var e = Of(id);
            e.Where = placement;
            e.HasWhere = true;
        }

        public bool TryWhere(string id, out Placement placement)
        {
            placement = default;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var e) || !e.HasWhere) return false;
            placement = e.Where;
            return true;
        }

        public bool HasWhere(string id)
            => !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var e) && e.HasWhere;

        /// <summary>Все известные места — арбитру слотов (чтобы двое не встали
        /// друг в друга) и диалогу (с какой стороны имя говорящего).</summary>
        public IEnumerable<KeyValuePair<string, Placement>> Wheres()
        {
            foreach (var kv in _byId)
                if (kv.Value.HasWhere)
                    yield return new KeyValuePair<string, Placement>(kv.Key, kv.Value.Where);
        }

        /// <summary>Все места, КУДА ПРОСИЛИ встать: они известны раньше арта, и
        /// имя говорящего встаёт с нужной стороны с первого кадра.</summary>
        public IEnumerable<KeyValuePair<string, Placement>> Targets()
        {
            foreach (var kv in _byId)
                if (kv.Value.HasTarget)
                    yield return new KeyValuePair<string, Placement>(kv.Key, kv.Value.Target);
        }

        /// <summary>Кого сцена вообще помнит — по любой из записей.</summary>
        public IEnumerable<string> Ids()
        {
            foreach (var kv in _byId) yield return kv.Key;
        }

        public void DropWhere(string id)
        {
            if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var e)) e.HasWhere = false;
        }

        // ── что надето ─────────────────────────────────────────────────────

        public void SetLook(string id, string look)
        {
            if (string.IsNullOrEmpty(id)) return;
            Of(id).Look = look;
        }

        public bool TryLook(string id, out string look)
        {
            look = null;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var e) || e.Look == null) return false;
            look = e.Look;
            return true;
        }

        public void DropLook(string id)
        {
            if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var e)) e.Look = null;
        }

        /// <summary>Забыть, что на ком надето, — но не самих людей.</summary>
        public void ForgetLooks()
        {
            foreach (var kv in _byId) kv.Value.Look = null;
        }

        // ── забвение ───────────────────────────────────────────────────────

        /// <summary>Забыть фигуру целиком. Раньше это были четыре строки в
        /// разных словарях, и забытая пятая оставляла тень человека.</summary>
        public void Forget(string id)
        {
            if (!string.IsNullOrEmpty(id)) _byId.Remove(id);
        }

        /// <summary>
        /// УБОРКА СЦЕНЫ: память ГЛАВЫ уходит, облик остаётся.
        ///
        /// <para>Разница не тонкость, а два вылеченных дефекта. Поза липкая, и
        /// команда, которой её ставило МЕНЮ (центр, рост витрины), переживала
        /// старт главы и подмешивалась к авторской — героиня выходила в сцену
        /// стоящей по-менюшному. А облик — свойство самой фигуры: героиня
        /// переживает уборку живой, слои на ней уже надеты, и стереть эту
        /// запись значило заставить её собираться заново на выходе из главы.</para>
        /// </summary>
        public void ForgetPoses()
        {
            var drop = new List<string>();
            foreach (var kv in _byId)
            {
                var e = kv.Value;
                e.Cmd = null;
                e.HasTarget = false;
                e.HasPose = false;
                e.HasWhere = false;
                if (e.Look == null) drop.Add(kv.Key);
            }
            foreach (var id in drop) _byId.Remove(id);
        }

        /// <summary>Забыть всех, кроме одного: тот, кто остаётся жить, уносит с
        /// собой и место, и облик — сцена по обе стороны перехода помнит ОДНУ
        /// И ТУ ЖЕ куклу.</summary>
        public void ForgetAllExcept(string keep)
        {
            var drop = new List<string>();
            foreach (var kv in _byId)
                if (!string.Equals(kv.Key, keep, System.StringComparison.Ordinal)) drop.Add(kv.Key);
            foreach (var id in drop) _byId.Remove(id);
        }
    }
}
