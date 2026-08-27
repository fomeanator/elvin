using System;
using System.Collections.Generic;

namespace Lvn.Services
{
    /// <summary>
    /// Функции выражений, которым нужен внешний мир: кошелёк, покупки, надетое.
    ///
    /// <para>Раньше выражения видели только переменные новеллы, и «эта ветка
    /// открыта, если платье куплено» написать было нечем — покупка живёт в
    /// кошельке на сервере, а не в статах истории.</para>
    ///
    /// <para>Живут они здесь, а не в ядре, по той же причине, по которой
    /// разделены сборки: движок не должен знать ни про кошелёк, ни про
    /// магазин. Новелла без монетизации остаётся работающей новеллой — просто
    /// эти функции в ней не зарегистрированы.</para>
    ///
    /// <para>Читают ЖИВОЕ состояние на каждый вызов. Снимок при старте главы
    /// был бы дешевле и был бы неверен: игрок покупает платье посреди сцены и
    /// должен увидеть ветку немедленно, а не после перезахода.</para>
    /// </summary>
    public static class LvnStoryFunctions
    {
        private static bool _installed;

        /// <summary>Ставит функции в вычислитель. Идемпотентно; цепляется к
        /// уже стоящему обработчику, а не затирает его — хост мог поставить
        /// свои.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            // Цепочка — в LvnExpression: свои функции добавляются, чужие
            // остаются. Своя копия этой сборки цепочки и была почти-дублем.
            Lvn.LvnExpression.AddHostFunction(Call);
        }

        private static object Call(string name, IReadOnlyList<object> args)
        {
            string S(int i) => i < args.Count ? args[i] as string ?? args[i]?.ToString() : null;
            switch (name)
            {
                // has_item("dress_red") — вещь в инвентаре кошелька. Инвентарь
                // считает штуки, поэтому «есть» — это больше нуля, а не
                // «ключ присутствует»: потраченная вещь остаётся ключом с нулём.
                case "has_item":
                {
                    var sku = S(0);
                    if (string.IsNullOrEmpty(sku)) return false;
                    return LvnWallet.Inventory.TryGetValue(sku, out var n) && n > 0;
                }

                // balance("crystals") — сколько валюты у игрока сейчас.
                case "balance":
                {
                    var cur = S(0);
                    if (string.IsNullOrEmpty(cur)) return 0d;
                    return LvnWallet.Balances.TryGetValue(cur, out var v) ? (double)v : 0d;
                }

                // worn("dress") или worn("hill", "dress") — что надето.
                // Один аргумент означает «на герое сцены»: в новелле про одну
                // героиню писать её id в каждом условии — лишний шум.
                case "worn":
                {
                    string entity = args.Count > 1 ? S(0) : DefaultEntity;
                    string axis = args.Count > 1 ? S(1) : S(0);
                    if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(axis)) return "";
                    // Примеренное важнее надетого: игрок стоит в гардеробе и
                    // крутит варианты — условие обязано видеть то же, что он
                    // видит на экране. Лесенку знает Костюмер, здесь её нет.
                    return Lvn.UI.LvnCostumer.Chosen(entity, axis);
                }
            }
            return Lvn.LvnExpression.NotHandled;
        }

        /// <summary>
        /// Герой, к которому относится <c>worn("ось")</c> без явного имени.
        /// Ставится оболочкой из настроек новеллы: у большинства историй
        /// гардероб один, и повторять его id в каждом условии незачем.
        /// </summary>
        public static string DefaultEntity { get; set; }
    }
}
