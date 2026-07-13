namespace Lvn
{
    /// <summary>
    /// A presentable choice option: its caption, the script index to pass back
    /// to <see cref="LvnPlayer.Choose"/>, and the optional narrative cost line
    /// shown beneath it. Options gated out by a stat threshold or an expression
    /// filter are not handed to the host at all.
    ///
    /// <para><see cref="WalletCurrency"/>/<see cref="WalletAmount"/> carry a
    /// REAL price (option field <c>wallet_cost: {currency, amount}</c> — e.g.
    /// an imported "[premium]" choice): picking the option must succeed a
    /// wallet spend through the host's spend hook first. <see cref="Cost"/>
    /// stays the purely narrative display line.</para>
    /// </summary>
    public readonly struct LvnOption
    {
        public readonly int Index;
        public readonly string Text;
        public readonly string Cost;
        public readonly string WalletCurrency; // null → free option
        public readonly long WalletAmount;

        public LvnOption(int index, string text, string cost,
            string walletCurrency = null, long walletAmount = 0)
        {
            Index = index;
            Text = text;
            Cost = cost;
            WalletCurrency = walletCurrency;
            WalletAmount = walletAmount;
        }
    }
}
