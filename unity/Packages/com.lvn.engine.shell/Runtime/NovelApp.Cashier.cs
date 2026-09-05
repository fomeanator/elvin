using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// КАССИР — берёт плату за вход и знает, что делать, если не хватило.
    ///
    /// <para>Обряд оплаты один и тот же везде, где игра просит деньги: списать;
    /// не хватило — объяснить, чего и сколько, и предложить магазин; после
    /// магазина попробовать снова; если и тогда нет — сказать прямо и не
    /// пустить. Порядок и формулировки были написаны ДВАЖДЫ — для входа в
    /// новеллу и для входа в главу, слово в слово, — и разошлись бы при первой
    /// же правке одного из них.</para>
    ///
    /// <para>Отдельным домом, потому что деньги — самостоятельная тема со своими
    /// правилами: что бесплатно (первая глава, уже оплаченный вход, отключённые
    /// ворота), как звучит отказ, и через сколько прибудет следующая энергия.
    /// Держать это внутри двухтысячестрочного <see cref="NovelApp"/>, который
    /// заодно грузит контент и ведёт главу, значило прятать кассу в подсобке.</para>
    ///
    /// <para>Кассир НЕ решает, сколько стоит вход (это данные новеллы:
    /// <c>title.cost</c>, <c>economy.chapter_cost</c>) и не ведёт счёт денег
    /// (это кошелёк, <c>LvnWallet</c>). Он проводит обряд.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // Charge a title's hub-entry cost (typically 1 energy for an expedition)
        // before it launches. Same store-retry flow as the per-chapter gate; free
        // when the title has no cost. Returns true if the player may enter.
        private async Task<bool> ChargeTitleEntryAsync(LvnTitle title)
        {
            // Цену считает ДОМ — тот же, что отвечает ценнику на кнопке.
            var cost = Lvn.Content.LvnEntryPrice.ForTitle(title);
            if (cost.Free) return true;
            // The entry was paid when the title was FIRST started — «Продолжить»
            // (or menu-exit + Play) must not charge the same entry again.
            // ВХОД УЖЕ ОПЛАЧЕН — спрашиваем НАЛИЧИЕ потолка, а не его величину.
            // Ноль это законный номер главы: у новеллы, чьи главы нумерованы с
            // нуля, потолок записан нулём, «больше нуля» ложно — и плата
            // бралась заново при КАЖДОМ возврате в уже оплаченную новеллу.
            if (LvnProgress.HasReached(title)) return true;

            return await ChargeWithStoreAsync(cost.Currency, cost.Amount,
                "title:" + title.id, "You need more to start this.");
        }

        /// <summary>
        /// ОБРЯД ОПЛАТЫ С МАГАЗИНОМ — тонкая обёртка над Кассиром
        /// (<see cref="Lvn.Services.LvnCashier"/>), который и держит порядок.
        ///
        /// <para>Здесь остаётся только то, что знает именно новелла: какими
        /// словами звучит отказ (их пишет автор в <c>economy.gate_*</c>), через
        /// сколько прибудет следующая энергия и куда вести за покупкой.</para>
        ///
        /// <para>Зовут отсюда трижды: вход в новеллу, ворота главы и платный
        /// выбор. Раньше третий шёл мимо — оттого и переименовано: «за вход»
        /// было верно, пока плательщик был один.</para>
        /// </summary>
        /// <param name="currency">чем платим</param>
        /// <param name="amount">сколько</param>
        /// <param name="reason">за что — уходит в журнал кошелька</param>
        /// <param name="fallbackMessage">объяснение, когда новелла своего не дала</param>
        private async Task<bool> ChargeWithStoreAsync(string currency, long amount,
                                                  string reason, string fallbackMessage)
        {
            if (string.IsNullOrEmpty(currency) || amount <= 0) return true; // бесплатно

            var eco = _manifest?.economy;
            var charge = new Lvn.Services.LvnCashier.Charge
            {
                Currency = currency,
                Amount = amount,
                Reason = reason,
                // ЧЕРЕЗ СЛОВАРЬ, а не полем напрямую: попап о нехватке —
                // такой же текст на экране, как любой другой, и каталог языка
                // обязан его доставать. Поле автора остаётся сильнее
                // умолчания, но слабее перевода.
                Title = LvnWords.Pick("economy.gate_title", eco?.gate_title, "Not enough energy"),
                Message = LvnWords.Pick("economy.gate_message", eco?.gate_message, fallbackMessage)
                          + RefillHint(currency),
                BuyText = LvnWords.Pick("economy.gate_buy", eco?.gate_buy, "Store"),
                CancelText = LvnWords.Pick("economy.gate_cancel", eco?.gate_cancel, "Not now"),
                DeniedTitle = eco?.gate_denied,
            };

            // Оболочки нет — спрашивать нечем: платит тот, у кого хватает, и
            // молча. Кассир вернёт NoOffer, и вход закроется без окна.
            if (_shell == null)
                return (await Lvn.Services.LvnCashier.ChargeAsync(charge, null, null)).Ok();

            var outcome = await Lvn.Services.LvnCashier.ChargeAsync(charge,
                (title, msg) => _shell.ConfirmAsync(title, msg, charge.BuyText, charge.CancelText),
                () => _shell.OpenPackShopAsync(),
                (title, msg) => _shell.AlertAsync(title, msg));
            return outcome.Ok();
        }

        // "⚡ +1 через 1 ч 20 мин" — the regen countdown for the gate popup, from the
        // wallet's computed refill state. Empty when the currency isn't regenerating.
        private static string RefillHint(string currency)
        {
            // Сколько ждать — спрашиваем у КОШЕЛЬКА: он один знает поправку на
            // часы устройства (сервер называет своё «сейчас»), а две копии
            // вычитания однажды разойдутся — и разошлись бы именно на игроке с
            // неверными часами.
            long rem = Lvn.Services.LvnWallet.SecondsUntilRefill(currency);
            if (rem <= 0) return "";
            // Словесный вид — тот же дом, что и цифровой в шапке: одно ожидание
            // не имеет права округляться в двух местах по-разному.
            return "\n\n" + LvnWords.Of("wallet.refill_in", "+1 in {t}")
                .Replace("{t}", Lvn.UI.LvnTimeWords.Coarse(rem));
        }

        // Charge the chapter-entry currency (typically the regenerating "energy")
        // before a fresh chapter loads. Returns true when the player may enter:
        // the gate is disabled, the chapter is free, the spend succeeded, or a
        // store purchase covered it. On a hard refusal (no funds and no/failed
        // purchase) shows a popup and returns false, dropping back to the carousel.
        private async Task<bool> ChargeChapterEntryAsync(LvnChapter chapter)
        {
            // Гейт, умолчание и список бесплатных глав — у ДОМА: карточка
            // новеллы показывает цену тем же расчётом, и разойтись им негде.
            var price = Lvn.Content.LvnEntryPrice.ForChapter(_manifest?.economy, chapter?.id);
            if (price.Free) return true;

            return await ChargeWithStoreAsync(price.Currency, price.Amount,
                "chapter:" + chapter?.id, "You need more to open this chapter.");
        }
    }
}
