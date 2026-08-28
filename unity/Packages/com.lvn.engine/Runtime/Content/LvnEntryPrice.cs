namespace Lvn.Content
{
    /// <summary>
    /// СКОЛЬКО СТОИТ ВОЙТИ — один расчёт и для ценника, и для списания.
    ///
    /// <para>Цену входа считали двое и по-разному. КАССИР при входе в главу
    /// берёт её из экономики манифеста (<c>economy.chapter_currency</c>,
    /// <c>chapter_cost</c>) и уважает список бесплатных глав. КАРТОЧКА новеллы
    /// показывала своё: цену новеллы (<c>title.cost</c>), а если её нет —
    /// собственное поле со значением 1, которое НИКТО никогда не задавал.</para>
    ///
    /// <para>То есть ценник был выдуман. Новелла без своей цены рисовала игроку
    /// «1» независимо от того, сколько спишется на самом деле, а бесплатная
    /// глава из <c>free_chapters</c> всё равно показывала цену — и списания
    /// потом не происходило. Оба случая читаются как обман, даже когда это
    /// просто рассинхрон двух правил.</para>
    ///
    /// <para>Здесь оба ответа: вход в НОВЕЛЛУ (её собственная цена) и вход в
    /// ГЛАВУ (гейт экономики). Показывающий и списывающий спрашивают одно и то
    /// же место — разойтись им больше негде.</para>
    /// </summary>
    public static class LvnEntryPrice
    {
        /// <summary>Цена входа: валюта и сумма. Пустая валюта или сумма ≤ 0 —
        /// вход бесплатный.</summary>
        public readonly struct Price
        {
            public readonly string Currency;
            public readonly long Amount;

            public Price(string currency, long amount)
            {
                Currency = currency;
                Amount = amount;
            }

            /// <summary>Ничего не спишется — и показывать нечего.</summary>
            public bool Free => string.IsNullOrEmpty(Currency) || Amount <= 0;

            public static readonly Price None = new Price(null, 0);
        }

        /// <summary>Вход в новеллу: её собственная цена из манифеста
        /// (<c>title.cost</c>). Нет цены — вход свободный, и выдумывать
        /// умолчание нельзя: это деньги игрока.</summary>
        public static Price ForTitle(LvnTitle title)
            => title?.cost == null || title.cost.amount <= 0
                ? Price.None
                : new Price(title.cost.currency, title.cost.amount);

        /// <summary>
        /// Вход в главу: гейт экономики. Валюта не названа — гейта нет вовсе;
        /// глава в <c>free_chapters</c> не стоит ничего (обучение и первая
        /// глава обычно там).
        /// </summary>
        public static Price ForChapter(LvnEconomyConfig economy, string chapterId)
        {
            var currency = economy?.chapter_currency;
            if (string.IsNullOrEmpty(currency)) return Price.None;   // гейт выключен
            int amount = economy.chapter_cost ?? 1;                  // умолчание объявлено манифестом
            if (amount <= 0) return Price.None;
            if (!string.IsNullOrEmpty(chapterId) && economy.free_chapters != null
                && economy.free_chapters.Contains(chapterId)) return Price.None;
            return new Price(currency, amount);
        }

        /// <summary>
        /// Что показать на кнопке «Играть»: цена новеллы, если она есть, иначе
        /// цена входа в главу. Порядок именно такой — своя цена новеллы
        /// перекрывает общий гейт, как и при списании.
        /// </summary>
        public static Price Shown(LvnTitle title, LvnEconomyConfig economy, string chapterId = null)
        {
            var own = ForTitle(title);
            return own.Free ? ForChapter(economy, chapterId) : own;
        }
    }
}
