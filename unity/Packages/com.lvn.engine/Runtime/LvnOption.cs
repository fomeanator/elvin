namespace Lvn
{
    /// <summary>
    /// A presentable choice option: its caption, the script index to pass back
    /// to <see cref="LvnPlayer.Choose"/>, and the optional narrative cost line
    /// shown beneath it. Options gated out by a stat threshold or an expression
    /// filter are not handed to the host at all.
    /// </summary>
    public readonly struct LvnOption
    {
        public readonly int Index;
        public readonly string Text;
        public readonly string Cost;

        public LvnOption(int index, string text, string cost)
        {
            Index = index;
            Text = text;
            Cost = cost;
        }
    }
}
