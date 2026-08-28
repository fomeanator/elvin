using System;
using System.Threading.Tasks;

namespace Lvn.Services
{
    /// <summary>
    /// КАССИР — единственная дорога от «игрок хочет платное» до «оплачено или нет».
    ///
    /// <para>Обряд один и тот же везде, где игра просит деньги: списать; не
    /// хватило — объяснить, чего и сколько, и предложить магазин; после магазина
    /// попробовать снова; если и тогда нет — сказать прямо. Записан он был
    /// ДВАЖДЫ и по-разному: ворота входа в новеллу/главу вели его одним
    /// порядком, покупка наряда — другим, и второй экземпляр сам себя выдавал
    /// комментарием «same pattern as the chapter/title entry gates».</para>
    ///
    /// <para>Копия успела разойтись с оригиналом на живом поведении: гардероб
    /// записывал исход покупки ПО ПЕРВОЙ попытке, поэтому покупка, состоявшаяся
    /// после захода в магазин, оставалась в отчёте провалом — событие успеха не
    /// писалось вовсе. Ровно то, чем опасны два экземпляра одного правила.</para>
    ///
    /// <para>Ответственность: провести обряд и назвать исход. Кассир НЕ решает,
    /// сколько стоит (это данные новеллы: <c>title.cost</c>,
    /// <c>economy.chapter_cost</c>, цена предмета каталога), НЕ ведёт счёт денег
    /// (это <see cref="LvnWallet"/>) и НЕ рисует попапы — окна показывает тот,
    /// кто позвал, своими средствами.</para>
    ///
    /// <para>Про учёт: успешное списание в отчёт пишет СЕРВЕР — оно ложится в
    /// журнал кошелька вместе с валютой, суммой и причиной, атомарно со снятием
    /// денег. Дублировать его клиентским событием нельзя: получилось бы два
    /// источника правды о деньгах, которые разойдутся на первом же потерянном
    /// пакете. А вот ОТКАЗ в журнале кошелька не появится никогда — списания не
    /// было, — поэтому «упёрся в цену» шлёт Кассир, и только его.</para>
    /// </summary>
    public static class LvnCashier
    {
        /// <summary>Чем кончился обряд.</summary>
        public enum Outcome
        {
            /// <summary>Платить было не за что: цена не назначена или ноль.</summary>
            Free,
            /// <summary>Хватило сразу.</summary>
            Paid,
            /// <summary>Не хватило, но игрок сходил в магазин — и хватило.</summary>
            PaidAfterStore,
            /// <summary>Не хватило, магазин предложен, игрок отказался.</summary>
            Declined,
            /// <summary>Не хватило даже после магазина.</summary>
            Denied,
            /// <summary>Не хватило, и предложить магазин было нечем: зовущий не
            /// дал, чем спрашивать. Не отказ игрока — недостача обряда, и
            /// объясняет её зовущий своими средствами.</summary>
            NoOffer,
        }

        /// <summary>Оплачено ли — три исхода из шести означают «пропустить».</summary>
        public static bool Ok(this Outcome o)
            => o == Outcome.Free || o == Outcome.Paid || o == Outcome.PaidAfterStore;

        /// <summary>
        /// ЧТО ПОКУПАЮТ И КАКИМИ СЛОВАМИ ОБ ЭТОМ ГОВОРИТЬ.
        ///
        /// <para>Цену и причину знает вызывающий — они из данных новеллы. Слова
        /// тоже его: у ворот главы они из <c>economy.gate_*</c>, у гардероба —
        /// из его конфигурации, и переводит их автор, а не движок.</para>
        /// </summary>
        public sealed class Charge
        {
            /// <summary>Чем платим. Пусто — платить не за что.</summary>
            public string Currency;
            /// <summary>Сколько. Ноль и меньше — бесплатно.</summary>
            public long Amount;
            /// <summary>За что — уходит в журнал кошелька («chapter:…», «wardrobe»).</summary>
            public string Reason;
            /// <summary>Какой предмет, если платят за вещь. Кошелёк запишет его
            /// в инвентарь — без этого покупка не станет владением.</summary>
            public string Sku;

            /// <summary>Заголовок отказа: «Не хватает энергии».</summary>
            public string Title;
            /// <summary>Объяснение целиком — вместе с подсказкой о пополнении.</summary>
            public string Message;
            /// <summary>Кнопка «в магазин».</summary>
            public string BuyText;
            /// <summary>Кнопка «не сейчас».</summary>
            public string CancelText;
            /// <summary>Заголовок окончательного отказа, если он свой.</summary>
            public string DeniedTitle;

            /// <summary>Чем пометить событие отказа сверх валюты и суммы —
            /// например, за какую героиню платили.</summary>
            public (string key, object value)[] Marks;
        }

        /// <summary>
        /// ПРОВЕСТИ ОБРЯД.
        ///
        /// <para>Окна показывает вызывающий: <paramref name="offerStore"/>
        /// спрашивает «пополнить?» и возвращает ответ игрока,
        /// <paramref name="openStore"/> открывает магазин и возвращается, когда
        /// игрок из него вышел, <paramref name="explain"/> сообщает об
        /// окончательном отказе. Не дал первых двух — обряд честно кончится
        /// <see cref="Outcome.NoOffer"/>, а не тихим «нет».</para>
        /// </summary>
        public static async Task<Outcome> ChargeAsync(Charge charge,
                                                     Func<string, string, Task<bool>> offerStore,
                                                     Func<Task> openStore,
                                                     Func<string, string, Task> explain = null)
        {
            if (charge == null) return Outcome.Free;
            if (string.IsNullOrEmpty(charge.Currency) || charge.Amount <= 0) return Outcome.Free;

            if (await LvnWallet.SpendAsync(charge.Currency, charge.Amount, charge.Reason, charge.Sku))
                return Outcome.Paid;

            if (offerStore == null || openStore == null) return Outcome.NoOffer;

            string title = charge.Title ?? "";
            string msg = charge.Message ?? "";
            if (!await offerStore(title, msg))
            {
                Deny(LvnEvents.SpendDeclined, charge);
                return Outcome.Declined;
            }

            await openStore();
            // Баланс после магазина знает сервер, а не наше зеркало: без сверки
            // только что купленные деньги остались бы невидимыми.
            await LvnWallet.RefreshAsync();
            if (await LvnWallet.SpendAsync(charge.Currency, charge.Amount, charge.Reason, charge.Sku))
                return Outcome.PaidAfterStore;

            if (explain != null) await explain(charge.DeniedTitle ?? title, msg);
            Deny(LvnEvents.SpendDenied, charge);
            return Outcome.Denied;
        }

        /// <summary>Отказ — единственное, что Кассир пишет в отчёт сам: в журнале
        /// кошелька его не будет, а знать, обо что игроки упираются, нужно.</summary>
        private static void Deny(string name, Charge charge)
        {
            var marks = charge.Marks ?? Array.Empty<(string, object)>();
            var props = new (string key, object value)[marks.Length + 3];
            props[0] = ("currency", charge.Currency);
            props[1] = ("amount", charge.Amount);
            props[2] = ("reason", charge.Reason ?? "");
            for (int i = 0; i < marks.Length; i++) props[i + 3] = marks[i];
            LvnAnalytics.Track(name, props);
        }
    }
}
