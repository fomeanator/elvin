using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// НАСТРОЕНИЕ ГЕРОИНИ ВИТРИНЫ (TR-66) — что она сейчас играет и почему.
    ///
    /// <para>Заказ Ильи: «управление эмоциями героини по событиям», «расписать
    /// события и их влияние», «настраивались эмоции и тайминги с помощью
    /// манифеста». Реакции — это СЦЕНЫ на языке движка (метки <c>on_store</c>,
    /// <c>on_idle</c>, <c>on_purchase</c>…), а здесь живёт правило выбора: что
    /// прерывает что и куда возвращаться, когда клип кончился. Разбор и выбор
    /// варианта — <c>docs/heroine-reactions.md</c>.</para>
    ///
    /// <para>Дом НИЧЕГО НЕ ИГРАЕТ и не знает про Unity: он отвечает на вопрос
    /// «какую метку играть сейчас». Проигрыватель и оболочка спрашивают.
    /// Такую машину можно прогнать стражем целиком, а именно в ней и живут
    /// ошибки: покупка, перебитая входом в комнату, или бездействие, которое
    /// не сбрасывается касанием.</para>
    /// </summary>
    public sealed class LvnMenuMood
    {
        /// <summary>Событие витрины: комната, действие игрока или тишина.</summary>
        public enum Kind
        {
            /// <summary>Игрок пришёл в комнату — настроение места.</summary>
            Room,
            /// <summary>Игрок что-то сделал: купил, переоделся, тронул героиню.</summary>
            Act,
            /// <summary>Игрок молчит — бездействие.</summary>
            Idle,
        }

        /// <summary>Метка комнаты, в которой игрок сейчас. Пусто — нейтральное
        /// лицо.</summary>
        public string RoomLabel { get; private set; }

        /// <summary>Что играет прямо сейчас; пусто — ничего.</summary>
        public string Playing { get; private set; }

        /// <summary>Какого рода то, что играет: по нему решается, можно ли
        /// перебить.</summary>
        public Kind PlayingKind { get; private set; }

        /// <summary>Сколько секунд игрок молчит. Считает оболочка тиком, здесь
        /// только порог.</summary>
        public float IdleSeconds { get; private set; }

        /// <summary>Через сколько секунд тишины героине становится скучно.
        /// Ноль — бездействие выключено.</summary>
        public float IdleAfter = 10f;

        /// <summary>Сколько секунд после скучающего клипа его не повторять:
        /// иначе героиня вздыхает по кругу, пока игрок читает.</summary>
        public float IdleCooldown = 25f;

        private float _idleBlockedFor;
        private bool _idlePlayed;

        /// <summary>Какие метки вообще есть в сценарии витрины. Нет метки —
        /// события не будет: обещать реакцию, которой не написали, значит
        /// гасить настроение комнаты ради пустоты.</summary>
        public HashSet<string> Known = new HashSet<string>();

        private bool Has(string label) => !string.IsNullOrEmpty(label) && Known.Contains(label);

        /// <summary>
        /// ИГРОК ПРИШЁЛ В КОМНАТУ. Настроение места держится, пока он тут, и
        /// уступает действиям: покупка важнее того, что он стоит в магазине.
        /// </summary>
        public string EnterRoom(string label)
        {
            RoomLabel = Has(label) ? label : null;
            Touch();                       // переезд — тоже признак жизни
            // Действие доигрывает: прерывать «покупку» приходом в комнату
            // значит отнимать у игрока то, ради чего он платил.
            if (PlayingKind == Kind.Act && Playing != null) return null;
            return Start(RoomLabel, Kind.Room);
        }

        /// <summary>ИГРОК ЧТО-ТО СДЕЛАЛ. Действие прерывает всё: и комнату, и
        /// скуку, и другое действие — как <c>on</c> в Ren'Py ATL.</summary>
        public string Act(string label)
        {
            Touch();
            return Has(label) ? Start(label, Kind.Act) : null;
        }

        /// <summary>ЛЮБОЕ КАСАНИЕ — героиня снова в настроении комнаты. Скука
        /// снимается тут же, но повтор придержан кулдауном.</summary>
        public void Touch()
        {
            IdleSeconds = 0f;
            if (_idlePlayed)
            {
                _idlePlayed = false;
                _idleBlockedFor = IdleCooldown;
            }
        }

        /// <summary>Тик времени. Возвращает метку, если пора заскучать.</summary>
        public string Tick(float dt, string idleLabel)
        {
            if (dt > 0f)
            {
                IdleSeconds += dt;
                if (_idleBlockedFor > 0f) _idleBlockedFor -= dt;
            }
            if (IdleAfter <= 0f || _idleBlockedFor > 0f || _idlePlayed) return null;
            if (IdleSeconds < IdleAfter || !Has(idleLabel)) return null;
            // Действие доигрывает: скука не перебивает того, что игрок вызвал
            // сам.
            if (PlayingKind == Kind.Act && Playing != null) return null;
            _idlePlayed = true;
            return Start(idleLabel, Kind.Idle);
        }

        /// <summary>
        /// КЛИП КОНЧИЛСЯ. Возвращаемся к настроению комнаты, а если его нет —
        /// к нейтральному лицу (пустая метка).
        /// </summary>
        public string Ended()
        {
            Playing = null;
            if (PlayingKind == Kind.Room) return null;   // комната уже играла — стоим
            PlayingKind = Kind.Room;
            return RoomLabel;
        }

        /// <summary>Игрок ушёл в главу: витрины нет, настроение забываем —
        /// вернётся он событием <c>on_return</c>.</summary>
        public void Leave()
        {
            Playing = null;
            RoomLabel = null;
            PlayingKind = Kind.Room;
            IdleSeconds = 0f;
            _idlePlayed = false;
            _idleBlockedFor = 0f;
        }

        private string Start(string label, Kind kind)
        {
            Playing = label;
            PlayingKind = kind;
            return label;
        }
    }
}
