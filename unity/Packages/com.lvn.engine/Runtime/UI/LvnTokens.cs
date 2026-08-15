using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Токены оформления — ОКНО В ДЕЙСТВУЮЩУЮ ТЕМУ, а не набор констант.
    ///
    /// <para>Раньше здесь лежали сами цвета, и это было ровно то место, где
    /// «тема» ломалась: хаб умел спрашивать тему, а остальные три сотни мест в
    /// оболочке читали отсюда жёстко зашитую «Полночь». Экран выходил
    /// наполовину перекрашенным — хуже, чем не перекрашенным вовсе.</para>
    ///
    /// <para>Теперь каждое значение отвечает <see cref="LvnTheme.Current"/>, и
    /// все прежние обращения <c>LvnTokens.Accent</c> продолжают работать, но
    /// стали темозависимыми — ни одно из них не пришлось править. Значения
    /// «Полночи» переехали в <see cref="LvnTheme.Midnight"/>, поэтому по
    /// умолчанию всё выглядит как раньше, до буквы.</para>
    ///
    /// <para>Сменить весь вид приложения = одно поле в манифесте
    /// (<c>ui.browse.theme</c>), а не правка этого файла.</para>
    /// </summary>
    public static class LvnTokens
    {
        // Нейтральные тона.
        public static Color Bg        => LvnTheme.Current.Bg;
        public static Color Surface   => LvnTheme.Current.Surface;
        public static Color SurfaceHi => LvnTheme.Current.SurfaceHi;
        public static Color Border    => LvnTheme.Current.Border;
        public static Color Text      => LvnTheme.Current.Text;
        public static Color TextDim   => LvnTheme.Current.TextDim;
        public static Color Faint     => LvnTheme.Current.Faint;

        // Акцент и чернила поверх него.
        public static Color Accent   => LvnTheme.Current.Accent;
        public static Color OnAccent => LvnTheme.Current.OnAccent;

        // Тёплое золото — суммы, премиальные метки, призыв к покупке.
        public static Color Gold     => LvnTheme.Current.Gold;

        // Перекрытия.
        public static Color Scrim   => LvnTheme.Current.Scrim;
        public static Color PanelBg => LvnTheme.Current.PanelBg;

        // Скругления. Были const; стали свойствами — константа не может
        // зависеть от темы, а зависеть обязана.
        public static float Radius   => LvnTheme.Current.Radius;
        public static float RadiusSm => LvnTheme.Current.RadiusSm;
    }
}
