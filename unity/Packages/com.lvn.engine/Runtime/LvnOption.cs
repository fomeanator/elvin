namespace Lvn
{
    /// <summary>One stat this option awards on pick ("+2 Матвей") — a preview
    /// only, computed by the importer from the branch's own immediate ops
    /// (see AnnotateChoiceEffects); never executed from here.</summary>
    public readonly struct LvnOptionEffect
    {
        public readonly string Label;
        public readonly int Delta;
        public LvnOptionEffect(string label, int delta) { Label = label; Delta = delta; }
    }

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
        /// <summary>Stat award preview ("+2 Матвей"), or null when the importer
        /// found nothing to preview (or the title has no stats configured).</summary>
        public readonly System.Collections.Generic.IReadOnlyList<LvnOptionEffect> Effects;

        public LvnOption(int index, string text, string cost,
            string walletCurrency = null, long walletAmount = 0,
            System.Collections.Generic.IReadOnlyList<LvnOptionEffect> effects = null)
        {
            Index = index;
            Text = text;
            Cost = cost;
            WalletCurrency = walletCurrency;
            WalletAmount = walletAmount;
            Effects = effects;
        }
    }
}
