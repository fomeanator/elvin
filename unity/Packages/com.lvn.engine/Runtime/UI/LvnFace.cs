using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// ВЫРАЖЕНИЕ ЛИЦА ГЕРОИНИ ВИТРИНЫ — временный слой эмоции поверх облика.
    ///
    /// <para>Реакция витрины (комната, действие, скука — <see cref="LvnMenuMood"/>)
    /// прежде шла на сцену обычной командой <c>actor id emotion=…</c>. У такой
    /// команды два свойства, которых у реакции быть не должно, и оба
    /// проявились 11.09.2026 на живой сборке:</para>
    /// <list type="bullet">
    /// <item>Команда без <c>x</c> наследует ЦЕЛЕВОЕ место фигуры и ставит её
    /// туда без плавности. Пришла через кадр после переезда комнаты — кукла
    /// вместо того, чтобы ехать, прыгнула в конец пути.</item>
    /// <item>Поле <c>emotion</c> команды — ось СЦЕНАРИЯ, а выбор фишкой в
    /// гардеробе — примерка. Примерка не перебивает ось, заданную сценарием
    /// (<see cref="LvnCostumer"/>): после первой реакции фишки эмоций
    /// переставали работать, а по концу любой реакции комната надевала своё
    /// лицо поверх выбранного игроком.</item>
    /// </list>
    ///
    /// <para>Здесь реакция — не команда, а НАЛОЖЕНИЕ: она знает только героя,
    /// ось лица и эмоцию. Положения у неё нет, в облик она не записывается,
    /// и снять её так же просто, как надеть. Порядок старшинства один и
    /// проверяемый: выбор игрока (примерка или надетое) &gt; реакция &gt; то,
    /// что написал сценарий или дал набор по умолчанию.</para>
    /// </summary>
    public static class LvnFace
    {
        private static readonly Dictionary<string, (string axis, string emotion)> _held
            = new Dictionary<string, (string, string)>();

        /// <summary>Надеть выражение: реакция просит лицо <paramref name="emotion"/>
        /// по оси <paramref name="axis"/>. Пустая эмоция — то же, что снять.</summary>
        public static void Hold(string entity, string axis, string emotion)
        {
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis)) return;
            if (string.IsNullOrEmpty(emotion)) { Release(entity); return; }
            _held[entity] = (axis, emotion);
        }

        /// <summary>Снять выражение — лицо возвращается к облику.</summary>
        public static void Release(string entity)
        {
            if (!string.IsNullOrEmpty(entity)) _held.Remove(entity);
        }

        /// <summary>Какая эмоция сейчас наложена; пусто — никакой.</summary>
        public static string Holding(string entity)
            => !string.IsNullOrEmpty(entity) && _held.TryGetValue(entity, out var h) ? h.emotion : null;

        /// <summary>
        /// ДЕРЖИТ ЛИ ЛИЦО САМ ИГРОК. Примерка фишкой или надетая через
        /// гардероб эмоция — его решение, и реакция ему не указ: комната не
        /// должна перебивать выбранное, а действие обязано к нему вернуться.
        /// </summary>
        public static bool PlayerHoldsFace(string entity, string axis)
            // Спрашиваем костюмера, а не записи гардероба: «примерка сильнее
            // надетого» — его лесенка. Без набора умолчаний она отвечает пусто,
            // когда игрок ничего не выбирал, — ровно тот вопрос, что здесь задан.
            => !string.IsNullOrEmpty(LvnCostumer.Chosen(entity, axis));

        /// <summary>
        /// Наложить выражение на собранные оси облика. Зовёт сцена при сборке
        /// арта — ПОСЛЕ костюмера, чтобы старшинство читалось в одном месте:
        /// игрок держит лицо — оси не трогаем; иначе реакция ложится поверх
        /// сценарного или дефолтного значения.
        /// </summary>
        public static void ApplyTo(Dictionary<string, string> axes, string entity)
        {
            if (axes == null || string.IsNullOrEmpty(entity)) return;
            if (!_held.TryGetValue(entity, out var h)) return;
            if (PlayerHoldsFace(entity, h.axis)) return;
            axes[h.axis] = h.emotion;
        }

        /// <summary>Забыть всё — для стендов и смены новеллы.</summary>
        public static void Clear() => _held.Clear();
    }
}
