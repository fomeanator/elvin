using System;
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
    /// и снять её так же просто, как надеть.</para>
    ///
    /// <para>ВЫБРАННОЕ ЛИЦО (TR-82, 14.09) живёт здесь же, а не в гардеробе:
    /// фишка в гардеробе меню выбирает лицо насовсем, оно запоминается на
    /// телефоне и стоит на героине в витрине, пока игрок не выберет другое или
    /// лицо по умолчанию. В гардероб оно не пишется НАРОЧНО: надетая ось
    /// дотягивается до главы (заполняет ось у команд сценария без эмоции), и
    /// 14.09 лицо героини прыгало между кадрами. В главе лицом командует
    /// сценарий — на время главы оболочка поднимает <see cref="InStory"/>, и
    /// ни выбор, ни реакции на актёров не ложатся.</para>
    ///
    /// <para>Порядок старшинства один и проверяемый: примерка в листе &gt;
    /// выбранное лицо &gt; реакция &gt; то, что написал сценарий витрины или
    /// дал набор по умолчанию.</para>
    /// </summary>
    public static class LvnFace
    {
        private static readonly Dictionary<string, (string axis, string emotion)> _held
            = new Dictionary<string, (string, string)>();

        /// <summary>ИСТОРИЯ ИДЁТ — лицом командует сценарий. Оболочка поднимает
        /// на время главы; пока поднят, <see cref="ApplyTo"/> не трогает
        /// актёров вовсе.</summary>
        public static bool InStory;

        /// <summary>Выбранное лицо сменилось (id героя): витрина переставляет
        /// куклу и кружки, как при смене наряда.</summary>
        public static event Action<string> Changed;

        private const string ChosenPrefix = "lvn_face_";

        /// <summary>Ключ в пространстве владельца (как у гардероба): лицо —
        /// личный выбор, на общем телефоне оно не достаётся следующему и
        /// уходит по «удалите меня».</summary>
        private static string ChosenKey(string entity) => Lvn.LvnKeep.Scoped(ChosenPrefix, entity);

        /// <summary>Выбрать лицо для витрины: пара ось/эмоция запоминается на
        /// телефоне. Пустая эмоция — забыть выбор.</summary>
        public static void Choose(string entity, string axis, string emotion)
        {
            if (string.IsNullOrEmpty(entity)) return;
            var key = ChosenKey(entity);
            if (string.IsNullOrEmpty(axis) || string.IsNullOrEmpty(emotion)) Lvn.LvnKeep.Drop(key);
            else Lvn.LvnKeep.Put(key, axis + "|" + emotion);
            Changed?.Invoke(entity);
        }

        /// <summary>Выбранное лицо героя: (ось, эмоция) или null, если не выбирали.</summary>
        public static (string axis, string emotion)? Chosen(string entity)
        {
            if (string.IsNullOrEmpty(entity)) return null;
            var raw = Lvn.LvnKeep.Get(ChosenKey(entity), "");
            int bar = raw.IndexOf('|');
            if (bar <= 0 || bar >= raw.Length - 1) return null;
            return (raw.Substring(0, bar), raw.Substring(bar + 1));
        }

        /// <summary>Эмоция выбранного лица; null — не выбирали.</summary>
        public static string ChosenEmotion(string entity) => Chosen(entity)?.emotion;

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

        /// <summary>Какая эмоция сейчас наложена реакцией; пусто — никакой.</summary>
        public static string Holding(string entity)
            => !string.IsNullOrEmpty(entity) && _held.TryGetValue(entity, out var h) ? h.emotion : null;

        /// <summary>
        /// ДЕРЖИТ ЛИ ЛИЦО САМ ИГРОК: примерка фишкой в листе или выбранное
        /// лицо витрины. Реакция ему не указ: комната не должна перебивать
        /// выбранное, а действие обязано к нему вернуться.
        /// </summary>
        public static bool PlayerHoldsFace(string entity, string axis)
            => !string.IsNullOrEmpty(LvnCostumer.Chosen(entity, axis)) || Chosen(entity) != null;

        /// <summary>
        /// Наложить лицо на собранные оси облика. Зовёт сцена при сборке арта
        /// — ПОСЛЕ костюмера, чтобы старшинство читалось в одном месте: идёт
        /// история — не трогаем ничего; примерка в листе — костюмер уже всё
        /// сказал; иначе выбранное лицо, иначе реакция.
        /// </summary>
        public static void ApplyTo(Dictionary<string, string> axes, string entity)
        {
            if (axes == null || string.IsNullOrEmpty(entity) || InStory) return;
            var chosen = Chosen(entity);
            if (chosen is (string ax, string em))
            {
                if (string.IsNullOrEmpty(LvnCostumer.Chosen(entity, ax))) axes[ax] = em;
                return;
            }
            if (!_held.TryGetValue(entity, out var h)) return;
            if (!string.IsNullOrEmpty(LvnCostumer.Chosen(entity, h.axis))) return;
            axes[h.axis] = h.emotion;
        }

        /// <summary>Забыть наложения и признак истории — для стендов и смены
        /// новеллы. Выбранное лицо живёт на телефоне и стирается только
        /// <see cref="Choose"/> с пустой эмоцией.</summary>
        public static void Clear() { _held.Clear(); InStory = false; }
    }
}
