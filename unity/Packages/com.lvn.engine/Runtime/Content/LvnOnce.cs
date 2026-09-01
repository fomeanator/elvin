using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lvn.Content
{
    /// <summary>
    /// ОДИН РАЗ НА АДРЕС: готовое отдаём, начатое разделяем.
    ///
    /// <para>Поставщик ассетов делает две вещи разом — помнит готовое и не даёт
    /// двум одновременным запросам одного адреса сделать работу дважды. Вторая
    /// половина не украшение: проигравший гонку перезаписывал запись кэша и
    /// НАВСЕГДА терял чужую текстуру — она оставалась в видеопамяти без единой
    /// ссылки. Утечка тихая, растёт с каждым таким совпадением и видна только
    /// как «под конец сессии всё тормозит».</para>
    ///
    /// <para><b>Правило было выучено один раз и применено тоже один раз.</b>
    /// Сетевой поставщик получил защиту от гонки; поставщик из каталога — нет,
    /// хотя грузит те же файлы тем же способом. У него стоит перепроверка кэша
    /// после ожидания, и она СУЖАЕТ окно, но не закрывает: оба захода проходят
    /// обе проверки, оба строят текстуру, один результат теряется. Одна работа,
    /// два поставщика, разные правила.</para>
    ///
    /// <para>Главный поток и только он: Unity возвращает продолжение туда же,
    /// откуда ушла задача, поэтому замков здесь нет и быть не должно — они
    /// создали бы вид защиты от того, чего не бывает.</para>
    /// </summary>
    public sealed class LvnOnce<T> where T : class
    {
        /// <summary>Готовое. Наружу — потому что правила ОСВОБОЖДЕНИЯ живут в
        /// своём доме (<see cref="AssetMemory"/>) и работают со словарём.</summary>
        public Dictionary<string, T> Done { get; } = new Dictionary<string, T>();

        private readonly Dictionary<string, Task<T>> _flying = new Dictionary<string, Task<T>>();

        public bool Has(string key) => !string.IsNullOrEmpty(key) && Done.ContainsKey(key);

        public T Get(string key)
            => !string.IsNullOrEmpty(key) && Done.TryGetValue(key, out var v) ? v : null;

        /// <summary>Готовое — сразу; идущее — общей задачей; нового — одного на
        /// всех просящих.</summary>
        public async Task<T> GetAsync(string key, Func<Task<T>> make)
        {
            if (string.IsNullOrEmpty(key) || make == null) return null;
            if (Done.TryGetValue(key, out var hit)) return hit;
            if (_flying.TryGetValue(key, out var pending))
            {
                // Отмена принадлежит ТОМУ, кто начал: для нас это просто промах.
                try { return await pending; }
                catch { return null; }
            }

            var task = make();
            _flying[key] = task;
            try
            {
                var made = await task;
                // НЕУДАЧУ НЕ ЗАПОМИНАЕМ. Пустой ответ означает «сейчас не
                // вышло», а не «этого нет»: запомнив его, мы отняли бы у файла
                // все будущие попытки — а именно так выглядит арт, который
                // «однажды не догрузился и больше не появился».
                if (made != null) Done[key] = made;
                return made;
            }
            finally { _flying.Remove(key); }
        }

        /// <summary>Положить готовое, минуя работу (пришло другим путём).</summary>
        public void Put(string key, T value)
        {
            if (!string.IsNullOrEmpty(key) && value != null) Done[key] = value;
        }
    }
}
