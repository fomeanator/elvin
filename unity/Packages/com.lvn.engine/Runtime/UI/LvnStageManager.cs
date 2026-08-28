using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Lvn;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ПОМРЕЖ — ведёт поток команд к сцене: кто отдаёт, о чём, чья побеждает.
    ///
    /// <para>Дверь в сцену была одна (<c>VnStage.ApplyStage</c>), но устроена
    /// как коммутатор: смотрит <c>op</c> и раздаёт по обработчикам. КТО прислал
    /// команду, она не знала и узнать не могла. А присылают шестеро — история,
    /// реплей сохранения, катсцены, витрина меню, гардероб, стражи — и у
    /// каждого свои мотивы. В споре побеждал тот, чей <c>await</c> вернулся
    /// позже.</para>
    ///
    /// <para>Отсюда весь класс дефектов, которые чинились поодиночке: рост
    /// героини скакал (три отправителя — три разные доли экрана), кукла прыгала
    /// при возврате в меню (три постановки подряд), реплика висела поверх
    /// катсцены ухода, агент пропадал на несколько ходов, поза витрины
    /// подмешивалась к авторской. Восемь симптомов, один корень: за поток
    /// команд целиком не отвечал никто.</para>
    ///
    /// <para><b>Что он делает.</b> Держит реестр занятых предметов («актёр
    /// victoria», «полотно», «окно реплики»), знает старшинство отправителей и
    /// пишет журнал решений: принято, отклонено, кем занято. Чего он НЕ делает:
    /// не решает, как выглядит облик (<see cref="LvnCostumer"/>), кто в кадре
    /// во время катсцены (Распорядитель, <c>VnStage.Solo</c>), что видно на
    /// экране (<see cref="LvnScreenDirector"/>), какого роста фигура
    /// (<see cref="LvnScale"/>). Он только пропускает и упорядочивает.</para>
    ///
    /// <para>Опирается на Хронометриста (<see cref="LvnStageClock"/>): часы
    /// отвечают, чья работа УСТАРЕЛА, помреж — кто ВАЖНЕЕ. Это разные вопросы,
    /// и раньше их путали.</para>
    /// </summary>
    public sealed class LvnStageManager
    {
        /// <summary>
        /// СТАРШИНСТВО. Чем больше, тем важнее команда при споре за один
        /// предмет.
        ///
        /// <para>Порядок выведен практикой, а не вкусом. Катсцена старше всех:
        /// это цельный кадр, который зритель смотрит от начала до конца, и
        /// вмешательство посреди него читается как сбой. Гардероб старше
        /// истории, потому что игрок ПРЯМО СЕЙЧАС смотрит на примерку и ждёт
        /// отклика на своё действие. Витрина меню младше истории: её мизансцена
        /// — оформление, а не событие. Страж младше всех: чинить он вправе лишь
        /// то, о чём никто не спорит, иначе самолечение перебивает живую
        /// работу.</para>
        /// </summary>
        public static int Rank(LvnSender s)
        {
            switch (s)
            {
                case LvnSender.Cutscene: return 100;
                case LvnSender.Wardrobe: return 80;
                case LvnSender.Story: return 60;
                case LvnSender.Replay: return 60;
                case LvnSender.Menu: return 40;
                default: return 10;   // Guard
            }
        }

        /// <summary>ЛИПКОЙ МОЖЕТ БЫТЬ ТОЛЬКО КОМАНДА ИСТОРИИ. Память сцены
        /// наследуется следующей авторской командой: место, размер, оси. Когда
        /// туда попадала команда витрины или гардероба, героиня выходила в
        /// главу стоящей по-менюшному — «не встраивается в игру, хотя её
        /// реплика». Чужие команды кадр меняют, память — нет.</summary>
        public static bool Sticky(LvnSender s) => s == LvnSender.Story || s == LvnSender.Replay;

        // ── реестр занятости ────────────────────────────────────────────────
        // Предмет занят, пока над ним идёт работа. Показ актёра асинхронный и
        // длится сотни миллисекунд — всё это время предмет чужой.
        private struct Claim
        {
            public LvnSender Sender;
            public float Until;      // realtime, когда держание истекает само
        }

        private readonly Dictionary<string, Claim> _holds = new Dictionary<string, Claim>();

        /// <summary>Сколько держится предмет, если держатель забыл отпустить.
        /// Страховка от вечной блокировки: молчаливо застрявший держатель хуже
        /// проигранного спора.</summary>
        public const float HoldSeconds = 6f;

        /// <summary>ПРЕДМЕТ КОМАНДЫ — то, за что идёт спор. Два `actor` про
        /// разных людей не конфликтуют вовсе, а `bg` и `bg3d` — про одно и то
        /// же полотно.</summary>
        public static string SubjectOf(JObject cmd)
        {
            var op = (string)cmd?["op"];
            if (string.IsNullOrEmpty(op)) return "?";
            // К чему относится команда, знает LvnOpKind; здесь остаётся только
            // перевод в КЛЮЧ предмета — то, за что спорят отправители.
            switch (LvnOpKind.Of(op))
            {
                case LvnOpSubject.Actor:
                    var id = (string)cmd["id"];
                    return string.IsNullOrEmpty(id) ? op : "actor:" + id;
                case LvnOpSubject.Background:
                    return "bg";
                case LvnOpSubject.Veil:
                    return "veil";      // вуали и эффекты кадра — один предмет
                default:
                    return op;
            }
        }

        /// <summary>
        /// ПУСТИТЬ ЛИ КОМАНДУ. Единственное место, где решается спор.
        ///
        /// <para>Отказ — это тоже решение, и он записывается: молчаливо
        /// отброшенная команда стоила нам не одного часа разборов.</para>
        /// </summary>
        public bool Admit(JObject cmd, LvnSender sender, out string why)
        {
            var subject = SubjectOf(cmd);
            why = null;
            if (_holds.TryGetValue(subject, out var hold))
            {
                if (LvnClock.Now() > hold.Until) _holds.Remove(subject);   // держатель молчит — отпускаем сами
                else if (Rank(sender) < Rank(hold.Sender))
                {
                    why = $"занято ({hold.Sender})";
                    // ГОЛОС АВТОРА НЕ ОТКЛОНЯЮТ — ЕГО ЖДУТ. Отказ означает, что
                    // команда пропала совсем: сценарий уехал дальше и второй раз
                    // её не отдаст. Так в кадре и оставались лишние люди —
                    // история сказала «скрыть», катсцена держала кадр, команда
                    // ушла в никуда (живой скрин Ильи: героиня и собеседник из
                    // прошлой сцены стоят посреди чужой реплики). Всем прочим
                    // отправителям отказ — нормальный ответ: витрина, страж и
                    // гардероб повторят своё сами, когда кадр освободится.
                    if (Sticky(sender)) { Defer(cmd, sender, subject, hold.Sender); return false; }
                    Note(cmd, sender, subject, "ОТКАЗ: " + why);
                    return false;
                }
            }
            Note(cmd, sender, subject, "принято");
            return true;
        }

        // ── очередь отложенного ─────────────────────────────────────────────
        // Команды автора, пришедшие на занятый предмет. Ждут освобождения и
        // играются в том же порядке, в каком их отдал сценарий.
        private readonly Dictionary<string, List<(JObject cmd, LvnSender sender)>> _deferred
            = new Dictionary<string, List<(JObject, LvnSender)>>();

        /// <summary>Сколько команд автора помещается в очередь одного предмета.
        /// Больше — значит что-то держит кадр непозволительно долго; лишнее
        /// отбрасывается с записью, чтобы очередь не росла бесконечно.</summary>
        public const int DeferLimit = 16;

        /// <summary>Кому отдавать отложенное, когда предмет освободится. Ставит
        /// сцена: помреж решает, ЧЬЯ команда играет, а исполняет её она.</summary>
        public System.Action<JObject, LvnSender> Apply { get; set; }

        private void Defer(JObject cmd, LvnSender sender, string subject, LvnSender holder)
        {
            if (!_deferred.TryGetValue(subject, out var q))
                _deferred[subject] = q = new List<(JObject, LvnSender)>();
            if (q.Count >= DeferLimit)
            {
                Note(cmd, sender, subject, $"ОТКАЗ: очередь полна ({holder} держит слишком долго)");
                return;
            }
            q.Add(((JObject)cmd.DeepClone(), sender));
            Note(cmd, sender, subject, $"отложено до {holder} (в очереди {q.Count})");
        }

        /// <summary>Отдать сцене всё, что ждало этот предмет.</summary>
        private void Flush(string subject)
        {
            if (Apply == null || !_deferred.TryGetValue(subject, out var q)) return;
            _deferred.Remove(subject);
            if (q.Count == 0) return;
            LvnLog.Trace($"[lvn-cmd] {subject}: свободен — доигрываем {q.Count} отложенных");
            foreach (var (cmd, sender) in q)
            {
                Note(cmd, sender, subject, "принято (из очереди)");
                Apply(cmd, sender);
            }
        }

        /// <summary>ЗАНЯТЬ ПРЕДМЕТ на время работы: катсцена держит кадр,
        /// гардероб — куклу. Старшего перебить нельзя.</summary>
        public void Hold(string subject, LvnSender sender)
        {
            if (string.IsNullOrEmpty(subject)) return;
            if (_holds.TryGetValue(subject, out var prev)
                && LvnClock.Now() <= prev.Until && Rank(sender) < Rank(prev.Sender)) return;
            _holds[subject] = new Claim { Sender = sender, Until = LvnClock.Now() + HoldSeconds };
        }

        /// <summary>Отпустить предмет — работа кончилась.</summary>
        public void Release(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return;
            _holds.Remove(subject);
            Flush(subject);
        }

        /// <summary>Отпустить всё, что держал этот отправитель (конец
        /// катсцены, закрытие гардероба).</summary>
        public void ReleaseAll(LvnSender sender)
        {
            List<string> drop = null;
            foreach (var kv in _holds)
                if (kv.Value.Sender == sender) (drop ??= new List<string>()).Add(kv.Key);
            if (drop == null) return;
            foreach (var k in drop) _holds.Remove(k);
            foreach (var k in drop) Flush(k);   // сперва отпустить всё, потом доигрывать
        }

        /// <summary>Уборка сцены: спорить больше не о чем.</summary>
        public void Clear()
        {
            _holds.Clear();
            // Уборка сцены — отложенному больше некуда играть: его кадра нет.
            _deferred.Clear();
        }

        /// <summary>Кто сейчас держит предмет (для диагностики и тестов);
        /// null — свободен.</summary>
        public LvnSender? HolderOf(string subject)
            => _holds.TryGetValue(subject, out var h) && LvnClock.Now() <= h.Until
               ? h.Sender : (LvnSender?)null;

        // ── журнал ──────────────────────────────────────────────────────────
        // Кольцо последних решений. В логе прекрасно видно, ЧТО применили, и
        // нигде не видно, КТО попросил и что отклонили: каждый разбор начинался
        // с гадания по стек-трейсам.
        private const int JournalSize = 64;
        private readonly Queue<string> _journal = new Queue<string>(JournalSize);

        private void Note(JObject cmd, LvnSender sender, string subject, string decision)
        {
            var line = $"{LvnClock.Now():0.00} {sender,-8} {(string)cmd?["op"] ?? "?",-8} "
                     + $"{subject,-18} {decision}";
            if (_journal.Count >= JournalSize) _journal.Dequeue();
            _journal.Enqueue(line);
            if (decision[0] == 'О') LvnLog.Trace("[lvn-cmd] " + line);   // отказ виден всегда
        }

        /// <summary>ПОТОК КОМАНД ЗА ПОСЛЕДНЕЕ ВРЕМЯ — кто, что и с каким
        /// решением. Первое, что стоит спросить, когда «на экране не то».</summary>
        public string Journal()
        {
            var sb = new System.Text.StringBuilder("[lvn-cmd] поток команд (свежие внизу):\n");
            foreach (var line in _journal) sb.Append("  ").Append(line).Append('\n');
            if (_holds.Count > 0)
            {
                sb.Append("  занято сейчас: ");
                foreach (var kv in _holds) sb.Append(kv.Key).Append('←').Append(kv.Value.Sender).Append(' ');
            }
            return sb.ToString();
        }
    }
}
