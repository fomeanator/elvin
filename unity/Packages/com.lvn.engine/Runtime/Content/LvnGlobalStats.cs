using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// КРОСС-НОВЕЛЛЬНЫЕ СТАТЫ — то, что игрок несёт из истории в историю.
    ///
    /// <para>Автор пишет их как <c>global.*</c>, и живут они не в главе, а в
    /// отдельной области хранилища. Отсюда правило, которое легко забыть:
    /// СТАТЫ НЕ ОТКАТЫВАЮТСЯ вместе с главой и не берутся из снимка сохранения
    /// — там они такие, какими были в момент записи, а другая новелла могла
    /// сдвинуть их с тех пор. Всегда грузим живые и накладываем поверх.</para>
    ///
    /// <para>Правило было записано ДВАЖДЫ разными словами — в откате главы к
    /// чекпойнту и в возобновлении с автосейва, — а ключ области (<c>__global</c>)
    /// и имя переменной (<c>global</c>) знали ещё двое. Четыре места про одно
    /// решение: первый признак пропущенного владельца.</para>
    ///
    /// <para>Ответственность: где статы лежат, как называются в скрипте и как
    /// накладываются на набор переменных. Что именно в них хранить — дело
    /// новеллы; когда сохранять — дело хроники главы.</para>
    /// </summary>
    public static class LvnGlobalStats
    {
        /// <summary>Область хранилища: отдельная от новелл, потому и «между
        /// ними». Подчёркивания — чтобы не столкнуться с id настоящей новеллы.</summary>
        public const string ScopeId = "__global";

        /// <summary>Корневая переменная скрипта: <c>global.rep</c> — это ключ
        /// «rep» внутри объекта «global».</summary>
        public const string VarName = "global";

        /// <summary>Прочитать живые статы. Пусто — игрок ещё ничего не набрал.</summary>
        public static Task<JObject> LoadAsync(ILvnStateStore store, CancellationToken ct = default)
            => store == null ? Task.FromResult<JObject>(null) : store.LoadVarsAsync(ScopeId, ct);

        /// <summary>Сохранить статы как есть.</summary>
        public static Task SaveAsync(ILvnStateStore store, JObject stats, CancellationToken ct = default)
            => store == null || stats == null ? Task.CompletedTask
             : store.SaveVarsAsync(ScopeId, stats, ct);

        /// <summary>
        /// НАЛОЖИТЬ ЖИВЫЕ СТАТЫ на набор переменных — единственный способ,
        /// которым они попадают в главу.
        ///
        /// <para>Пустые не накладываются: пустой объект затёр бы то, что уже
        /// лежит в наборе, и превратил бы «игрок ничего не набрал в этой
        /// сессии» в «игрок потерял всё».</para>
        /// </summary>
        public static async Task OverlayAsync(ILvnStateStore store, JObject target,
                                              CancellationToken ct = default)
        {
            if (store == null || target == null) return;
            var live = await LoadAsync(store, ct);
            if (live != null && live.Count > 0) target[VarName] = live;
        }
    }
}
