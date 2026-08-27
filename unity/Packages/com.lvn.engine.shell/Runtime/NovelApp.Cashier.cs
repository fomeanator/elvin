using System.Threading.Tasks;
using Lvn.Content;

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
            var cost = title?.cost;
            if (cost == null || string.IsNullOrEmpty(cost.currency) || cost.amount <= 0) return true;
            // The entry was paid when the title was FIRST started — «Продолжить»
            // (or menu-exit + Play) must not charge the same entry again.
            if (LvnProgress.Reached(title) > 0) return true;

            return await ChargeEntryAsync(cost.currency, cost.amount,
                "title:" + title.id, "You need more to start this.");
        }

        /// <summary>
        /// ОБРЯД ОПЛАТЫ — единственный способ взять с игрока деньги за вход.
        ///
        /// <para>Списать; если не хватило — объяснить, чего и сколько, и
        /// предложить магазин; после магазина попробовать ещё раз; если и тогда
        /// нет — сказать об этом прямо. Порядок и формулировки были написаны
        /// дважды, для входа в новеллу и в главу, слово в слово, — и разошлись
        /// бы при первой же правке одного из них.</para>
        /// </summary>
        /// <param name="currency">чем платим</param>
        /// <param name="amount">сколько</param>
        /// <param name="reason">за что — уходит в кошелёк и в аналитику</param>
        /// <param name="fallbackMessage">объяснение, когда новелла своего не дала</param>
        private async Task<bool> ChargeEntryAsync(string currency, long amount,
                                                  string reason, string fallbackMessage)
        {
            if (string.IsNullOrEmpty(currency) || amount <= 0) return true; // бесплатно
            if (await Lvn.Services.LvnWallet.SpendAsync(currency, amount, reason)) return true;
            if (_shell == null) return false;

            var eco = _manifest?.economy;
            string title = eco?.gate_title ?? "Not enough energy";
            string msg = (eco?.gate_message ?? fallbackMessage) + RefillHint(currency);
            bool toStore = await _shell.ConfirmAsync(title, msg,
                eco?.gate_buy ?? "Store", eco?.gate_cancel ?? "Not now");
            if (!toStore) return false;

            await _shell.OpenPackShopAsync();
            await Lvn.Services.LvnWallet.RefreshAsync();
            if (await Lvn.Services.LvnWallet.SpendAsync(currency, amount, reason)) return true;

            await _shell.AlertAsync(eco?.gate_denied ?? title, msg);
            return false;
        }

        // "⚡ +1 через 1 ч 20 мин" — the regen countdown for the gate popup, from the
        // wallet's computed refill state. Empty when the currency isn't regenerating.
        private static string RefillHint(string currency)
        {
            if (!Lvn.Services.LvnWallet.Regen.TryGetValue(currency, out var r) || r.NextRefillUnix <= 0) return "";
            long rem = r.NextRefillUnix - System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (rem <= 0) return "";
            long h = rem / 3600, m = (rem % 3600) / 60;
            return "\n\n+1 энергия через " + (h > 0 ? h + " ч " + m + " мин" : m + " мин");
        }

        // Charge the chapter-entry currency (typically the regenerating "energy")
        // before a fresh chapter loads. Returns true when the player may enter:
        // the gate is disabled, the chapter is free, the spend succeeded, or a
        // store purchase covered it. On a hard refusal (no funds and no/failed
        // purchase) shows a popup and returns false, dropping back to the carousel.
        private async Task<bool> ChargeChapterEntryAsync(LvnChapter chapter)
        {
            var eco = _manifest?.economy;
            var currency = eco?.chapter_currency;
            int cost = eco?.chapter_cost ?? 1;
            if (string.IsNullOrEmpty(currency) || cost <= 0) return true; // gate off
            if (eco.free_chapters != null && chapter != null && eco.free_chapters.Contains(chapter.id))
                return true; // this chapter is on the house

            return await ChargeEntryAsync(currency, cost,
                "chapter:" + chapter?.id, "You need more to open this chapter.");
        }
    }
}
