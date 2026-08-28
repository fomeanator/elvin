using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// КАДР — что стоит на сцене прямо сейчас, записанное явно.
    ///
    /// <para>Раньше этого не существовало. Состояние сцены было СЛЕДОМ от
    /// последовательности команд: кто скомандовал последним, тот и прав. Пока
    /// командовал один сценарий, это работало; но в кадр вмешиваются ещё
    /// четверо — катсцены, витрина меню, гардероб и стражи, — и после их
    /// вмешательства история уже не знает, каким она оставила кадр. Отсюда
    /// целый класс дефектов, который мы чинили поштучно: «агент пропал на
    /// несколько ходов», «героиня не уходит, хотя не её реплика», «вернулся без
    /// грима», «поза витрины подмешалась к авторской».</para>
    ///
    /// <para>Здесь кадр — ДАННЫЕ: кто в нём, чем поставлен, каким гримом
    /// покрыт, какой фон и какая вуаль. Данные можно сложить, сравнить и
    /// вернуть в прежнее состояние — со следом от команд ничего этого сделать
    /// нельзя.</para>
    ///
    /// <para>Чистая модель без Unity: её видно в тестах целиком, и она
    /// одинаково верна для канваса, для будущего рендерера и для реплея.</para>
    /// </summary>
    public sealed class LvnFrame
    {
        /// <summary>Один человек (или предмет) в кадре.</summary>
        public struct Actor
        {
            /// <summary>Команда, которой его поставили: место, размер, оси
            /// облика. Она же — то, чем его можно поставить снова.</summary>
            public JObject Pose;

            /// <summary>Грим: тёмный силуэт, голограмма, обводка, растворение.
            /// Живёт ОТДЕЛЬНО от позы, потому что приходит своей командой
            /// (<c>sfx</c>) — и именно поэтому терялся при возврате кадра.</summary>
            public JObject Fx;

            /// <summary>Виден ли. Скрытый остаётся в кадре как запись: сцена
            /// помнит, чем его вернуть, и это не то же самое, что «его нет».</summary>
            public bool Visible;
        }

        /// <summary>Кто в кадре, по id. Порядок словаря значения не имеет —
        /// порядок слоёв решает z и старшинство рождения.</summary>
        public readonly Dictionary<string, Actor> Actors = new Dictionary<string, Actor>();

        /// <summary>Полотно: последняя команда фона (или null — «фона нет»).</summary>
        public JObject Background;

        /// <summary>Вуаль и эффекты кадра — затемнение, глитч, блюр.</summary>
        public JObject Veil;

        /// <summary>ПУСТ ЛИ СЛОЙ. Наложение, которое ничего не говорит, не
        /// должно ничего и менять — иначе открытая пустая катсцена стирала бы
        /// кадр под собой.</summary>
        public bool IsEmpty => Actors.Count == 0 && Background == null && Veil == null && !Exclusive;

        /// <summary>
        /// «В КАДРЕ ТОЛЬКО МОИ». Катсцена и гардероб говорят это про себя:
        /// остальные не убираются насовсем, а скрываются на время наложения.
        ///
        /// <para>Это и есть замена «увести и не забыть вернуть»: под
        /// наложением слой истории остаётся целым, и по закрытии проступает
        /// сам — с позами, гримом и порядком.</para>
        /// </summary>
        public bool Exclusive;

        public LvnFrame Clone()
        {
            var c = new LvnFrame { Exclusive = Exclusive };
            foreach (var kv in Actors)
                c.Actors[kv.Key] = new Actor
                {
                    Pose = kv.Value.Pose?.DeepClone() as JObject,
                    Fx = kv.Value.Fx?.DeepClone() as JObject,
                    Visible = kv.Value.Visible,
                };
            c.Background = Background?.DeepClone() as JObject;
            c.Veil = Veil?.DeepClone() as JObject;
            return c;
        }

        /// <summary>Записать команду в кадр. Возвращает false, если команда не
        /// про состояние кадра (звук, ожидание, реплика) — такие идут мимо
        /// модели прямо к исполнителю.</summary>
        /// <summary>ТОТ ЖЕ ЛИ ЭТО КАДР. Нужен расписанию: узел, к которому
        /// пришли двумя путями, считается известным, только если сцена в нём
        /// совпала. Сравниваются данные, а не ссылки, — иначе одинаковые кадры
        /// объявлялись бы разными на каждом клонировании.</summary>
        /// <summary>Кто виден в кадре, по алфавиту — для логов и сравнений.
        /// Читаемая строка о составе кадра стоит десяти строк стек-трейса,
        /// когда разбираешься, кто на экране лишний.</summary>
        public List<string> Visible()
        {
            var out_ = new List<string>();
            foreach (var kv in Actors) if (kv.Value.Visible) out_.Add(kv.Key);
            out_.Sort(System.StringComparer.Ordinal);
            return out_;
        }

        public bool SameAs(LvnFrame other)
        {
            if (other == null || Actors.Count != other.Actors.Count) return false;
            if (!JToken.DeepEquals(Background, other.Background)) return false;
            if (!JToken.DeepEquals(Veil, other.Veil)) return false;
            foreach (var kv in Actors)
            {
                if (!other.Actors.TryGetValue(kv.Key, out var b)) return false;
                if (kv.Value.Visible != b.Visible) return false;
                if (!JToken.DeepEquals(kv.Value.Pose, b.Pose)) return false;
                if (!JToken.DeepEquals(kv.Value.Fx, b.Fx)) return false;
            }
            return true;
        }

        public bool Absorb(JObject cmd)
        {
            var op = (string)cmd?["op"];
            if (string.IsNullOrEmpty(op)) return false;
            // Фон и вуаль опознаёт LvnOpKind: список «что считается вуалью» жил
            // и здесь, и у Распорядителя сцены, и они уже начали расходиться.
            if (LvnOpKind.IsBackground(op)) { Background = (JObject)cmd.DeepClone(); return true; }
            if (LvnOpKind.IsVeil(op)) { Veil = (JObject)cmd.DeepClone(); return true; }
            switch (op)
            {
                case "actor":
                case "obj":
                {
                    var id = (string)cmd["id"];
                    if (string.IsNullOrEmpty(id)) return false;
                    Actors.TryGetValue(id, out var a);
                    a.Pose = (JObject)cmd.DeepClone();
                    // Было голым (bool): авторское «show=no» здесь не давало
                    // ложь, а бросало исключение на разборе кадра.
                    a.Visible = LvnBool.Of(cmd["show"], true);
                    Actors[id] = a;
                    return true;
                }
                case "sfx":
                {
                    var id = (string)cmd["id"];
                    if (string.IsNullOrEmpty(id)) return false;
                    Actors.TryGetValue(id, out var a);
                    // Полное `off` снимает грим, а не становится им.
                    bool off = cmd["off"] != null && string.IsNullOrEmpty((string)cmd["part"]);
                    a.Fx = off ? null : (JObject)cmd.DeepClone();
                    Actors[id] = a;
                    return true;
                }
                case "clear":
                    // Уходят все, но кадр помнит, чем их вернуть: `clear` — это
                    // «скрыть всех», а не «забыть всех». Разница видна ровно
                    // тогда, когда сценарий показывает кого-то снова без
                    // position — он встаёт на своё прежнее место.
                    var ids = new List<string>(Actors.Keys);
                    foreach (var id in ids)
                    {
                        var a = Actors[id];
                        a.Visible = false;
                        Actors[id] = a;
                    }
                    return true;
                default:
                    return false;
            }
        }
    }
}
