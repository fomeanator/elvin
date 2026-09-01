using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ДОСКА ЗАКРЕПЛЕНИЯ — что держать в памяти, пока оно на экране.
    ///
    /// <para>Стриминговое окно выгружает давно не запрошенный арт, и это верно:
    /// память телефона кончается быстрее терпения. Но «давно не запрошенный» и
    /// «не нужный» — разные вещи: показанному арту запросы больше не приходят,
    /// он просто ВИСИТ НА ЭКРАНЕ. Отсюда белые квадраты вместо героини и
    /// полотно, побелевшее посреди кадра.</para>
    ///
    /// <para><b>Один механизм, три хозяина.</b> Держать набор спрайтов под
    /// ключом умели три места по трём разным правилам: сцена (ключ — слот,
    /// отпускание с задержкой), скелеты (ключ — актёр, отпускание сразу) и
    /// картинка в панели (ключ — сам элемент). Одинаковой была работа,
    /// разными — только ключ и задержка; и там, где работу писали заново,
    /// правило порядка теряли.</para>
    ///
    /// <para><b>ПРИКРЕПИТЬ РАНЬШЕ, ЧЕМ ОТПУСТИТЬ.</b> Наборы пересекаются:
    /// пересборка облика часто оставляет те же слои, а перестройка скелета —
    /// те же страницы атласа. Отпустив прежний набор первым, мы на мгновение
    /// доводим счётчик общего спрайта до нуля — и стриминговое окно вправе
    /// забрать текстуру ровно в этот момент. Сцена это знала и делала верно;
    /// скелеты делали наоборот. Теперь порядок один и переписать его негде.
    /// </para>
    ///
    /// <para><b>ОТПУСКАЕТ ТОТ, КТО ДЕРЖАЛ.</b> Доска помнит счётчик держателей
    /// вместе с набором. Смена содержимого (обновление, другая ступень
    /// качества) меняет загрузчика под ногами, и снятие через ТЕКУЩИЙ вернуло
    /// бы счёт не туда: прежний держал бы текстуру вечно, а новому пришёл бы
    /// минус за то, чего он не давал.</para>
    ///
    /// <para>Доска знает про загрузчик ровно одно действие
    /// (<see cref="ILvnPinLedger"/>) — «плюс или минус держатель». Ни кэш, ни
    /// стриминговое окно, ни отставленные записи её не касаются.</para>
    /// </summary>
    public sealed class LvnPinBoard<TKey>
    {
        private sealed class Held
        {
            public ILvnPinLedger Ledger;
            public List<Sprite> Sprites;
        }

        private readonly Dictionary<TKey, Held> _held;
        private readonly float _releaseAfter;

        /// <param name="releaseAfterSeconds">Через сколько отпускать ПРЕЖНИЙ
        /// набор. Ноль — сразу. Задержка нужна там, где уходящее ещё видно:
        /// прокси смены облика показывает старые слои весь кроссфейд, и
        /// мгновенное отпускание отдавало их окну прямо под ним — актёр
        /// вставал белым прямоугольником (живой скрин 27.08).</param>
        public LvnPinBoard(float releaseAfterSeconds = 0f, IEqualityComparer<TKey> keys = null)
        {
            _releaseAfter = releaseAfterSeconds;
            _held = keys != null ? new Dictionary<TKey, Held>(keys) : new Dictionary<TKey, Held>();
        }

        /// <summary>Держать этот набор под этим ключом. Пустой набор —
        /// то же, что <see cref="Release"/>.</summary>
        public void Hold(TKey key, ILvnPinLedger ledger, IReadOnlyList<Sprite> sprites)
        {
            List<Sprite> keep = null;
            if (ledger != null && sprites != null && sprites.Count > 0)
            {
                keep = new List<Sprite>(sprites.Count);
                foreach (var s in sprites)
                    if (s != null) { ledger.PinSprite(s, true); keep.Add(s); }
                if (keep.Count == 0) keep = null;
            }

            if (_held.TryGetValue(key, out var prev)) Let(prev);

            if (keep == null) _held.Remove(key);
            else _held[key] = new Held { Ledger = ledger, Sprites = keep };
        }

        /// <summary>Отпустить набор под ключом. Ключа нет — ничего не делаем.</summary>
        public void Release(TKey key)
        {
            if (!_held.TryGetValue(key, out var held)) return;
            _held.Remove(key);
            Let(held);
        }

        /// <summary>Отпустить всё. Задержка не применяется: доску очищают
        /// тогда, когда держать больше нечего.</summary>
        public void ReleaseAll()
        {
            foreach (var held in _held.Values) Unpin(held);
            _held.Clear();
        }

        public bool Holds(TKey key) => _held.ContainsKey(key);

        public IReadOnlyList<Sprite> Of(TKey key)
            => _held.TryGetValue(key, out var h) ? h.Sprites : null;

        /// <summary>Ключи КОПИЕЙ: обходя доску, обычно как раз и отпускают —
        /// а править словарь во время его обхода нельзя.</summary>
        public List<TKey> Keys()
        {
            var list = new List<TKey>(_held.Count);
            foreach (var k in _held.Keys) list.Add(k);
            return list;
        }

        private void Let(Held held)
        {
            if (_releaseAfter <= 0f) { Unpin(held); return; }
            LvnAsync.Fire(LetLaterAsync(held), "PinBoardRelease");
        }

        private async Task LetLaterAsync(Held held)
        {
            await Task.Delay((int)(_releaseAfter * 1000f));
            Unpin(held);
        }

        private static void Unpin(Held held)
        {
            if (held?.Ledger == null || held.Sprites == null) return;
            foreach (var s in held.Sprites) held.Ledger.PinSprite(s, false);
        }
    }
}
