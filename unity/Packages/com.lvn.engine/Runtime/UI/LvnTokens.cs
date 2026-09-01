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

        /// <summary>Поверхность, сквозь которую видно сцену: та же, но не
        /// глухая. Собиралась по месту из трёх составляющих цвета и числа
        /// 0.88 — дважды, и каждый раз заново.</summary>
        public static Color SurfaceSoft
        {
            get { var c = Surface; return UiColor.WithAlpha(c, 0.88f); }
        }
        public static Color Border    => LvnTheme.Current.Border;
        public static Color Text      => LvnTheme.Current.Text;
        public static Color TextDim   => LvnTheme.Current.TextDim;
        public static Color Faint     => LvnTheme.Current.Faint;

        // Акцент и чернила поверх него.
        public static Color Accent   => LvnTheme.Current.Accent;
        public static Color OnAccent => LvnTheme.Current.OnAccent;

        // Тёплое золото — суммы, премиальные метки, призыв к покупке.
        public static Color Gold     => LvnTheme.Current.Gold;
        public static Color Silver   => LvnTheme.Current.Silver;
        public static Color Bronze   => LvnTheme.Current.Bronze;

        /// <summary>ЦВЕТ МЕСТА НА ПОДИУМЕ — золото, серебро, бронза; дальше
        /// третьего подиума нет, и место без медали берёт тихую грань.
        ///
        /// <para>Вопрос задавали дважды в одной постройке — кольцо аватара и
        /// значок номера, — и оба раза писали серебро с бронзой числами прямо
        /// в тернарнике, хотя золото рядом уже приходило токеном. Тема, у
        /// которой золото своё, а серебро навсегда белое, — половина
        /// темы.</para></summary>
        public static Color Medal(int place) =>
            place == 1 ? Gold : place == 2 ? Silver : place == 3 ? Bronze : Border;

        // Перекрытия.
        /// <summary>Смысловые цвета: получилось и беда. Отдельны от акцента —
        /// акцент это тон новеллы, а красный это исход действия.</summary>
        public static Color Ok  => LvnTheme.Current.Ok;
        public static Color Bad => LvnTheme.Current.Bad;

        public static Color Scrim   => LvnTheme.Current.Scrim;
        public static Color PanelBg => LvnTheme.Current.PanelBg;
        public static Color Track   => LvnTheme.Current.Track;

        /// <summary>ЗАТЕМНЕНИЕ нужной плотности — тон берётся у темы, alpha
        /// задаёт вызывающий. Плотностей в оболочке много (подложка панели,
        /// пилюля над сценой, полоса HUD), и каждая писалась своим
        /// <c>new Color(0,0,0,0.5f)</c> — то есть мимо темы: на светлой или
        /// холодной палитре такие места остаются чёрными пятнами.</summary>
        public static Color Veil(float alpha)
        {
            var s = LvnTheme.Current.Scrim;
            return UiColor.WithAlpha(s, Mathf.Clamp01(alpha));
        }

        /// <summary>
        /// ТОН ПАНЕЛИ с заданной прозрачностью — тот же цвет, что у листов и
        /// диалога, но своей плотности.
        ///
        /// <para>Тон был вписан ЧИСЛАМИ в трёх местах: палитра оболочки, тема
        /// сцены и поле ввода имени. Прозрачность у каждого своя и это законно
        /// (лист плотнее диалога), а вот сам цвет — один факт, записанный
        /// трижды. Причём в теме сцены рядом стоит обещание «умолчания взяты из
        /// токенов Полночи», которое числами и нарушалось: смени тему — и поле
        /// ввода осталось бы прежнего цвета посреди новой палитры.</para>
        /// </summary>
        public static Color Panel(float alpha)
        {
            var p = LvnTheme.Current.PanelBg;
            return UiColor.WithAlpha(p, Mathf.Clamp01(alpha));
        }

        // Скругления. Были const; стали свойствами — константа не может
        // зависеть от темы, а зависеть обязана.
        public static float RadiusXs => LvnTheme.Current.RadiusXs;
        public static float Radius   => LvnTheme.Current.Radius;
        public static float RadiusSm => LvnTheme.Current.RadiusSm;
        public static float RadiusLg => LvnTheme.Current.RadiusLg;
        /// <summary>«Таблетка» — скругление во всю высоту.</summary>
        public static float RadiusPill => LvnTheme.RadiusPill;

        // Типографская шкала и шкала отступов. Здесь же, а не в каждом экране:
        // размер — такая же часть темы, как цвет.
        // Кегли идут через ПОПРАВКУ ГАРНИТУРЫ: рукописная при том же числе
        // выглядит вдвое мельче гротеска, пиксельная — крупнее и шире. Без
        // поправки выбор шрифта ломал бы вёрстку там, где до него всё
        // помещалось.
        // Нижняя ступень (бейдж, счётчик, метка на плитке) добавлена 01.09:
        // без неё мелкое (12–17) прыгало сразу на 20 — а это уже другой
        // размер, а не та же вещь.
        public static int TextMicro   => LvnFonts.Size(LvnTheme.Current.TextMicro);
        public static int TextXs      => LvnFonts.Size(LvnTheme.Current.TextXs);
        public static int TextSm      => LvnFonts.Size(LvnTheme.Current.TextSm);
        public static int TextBase    => LvnFonts.Size(LvnTheme.Current.TextBase);
        public static int TextLg      => LvnFonts.Size(LvnTheme.Current.TextLg);
        public static int TextXl      => LvnFonts.Size(LvnTheme.Current.TextXl);
        public static int TextDisplay => LvnFonts.Size(LvnTheme.Current.TextDisplay);

        /// <summary>Кегль, НАЗВАННЫЙ АВТОРОМ, — или ступень лестницы, если он
        /// промолчал.
        ///
        /// <para>Авторское число тоже проходит через <see cref="LvnFonts.Size"/>:
        /// правило «один кегль — один видимый размер у любой гарнитуры» держится
        /// оптической поправкой, и шесть настраиваемых заголовков были
        /// единственными, кто мимо неё ходил — при смене гарнитуры они одни
        /// менялись в размере.</para>
        ///
        /// <para>А умолчание за <c>??</c> было ещё и дырой в лестнице: страж
        /// видит <c>fontSize = 22f</c>, но не видит <c>fontSize = cfg.x ?? 22f</c>,
        /// и мимо шкалы жили 22, 34 и 40.</para>
        /// </summary>
        public static int TextOr(float? authored, int step) =>
            authored.HasValue ? LvnFonts.Size(authored.Value) : step;

        /// <summary>Волосяной зазор (2) — притирка: разделитель, кромка, точка.
        /// Ниже нижней ступени шкалы, и потому со своим именем: это не «мало
        /// воздуха», а его отсутствие с оговоркой.</summary>
        public static float Hair => LvnTheme.Current.Hair;

        /// <summary>Тесный зазор (4) — иконка у слова, чип в ряду. Второе и
        /// последнее значение ниже шкалы: их было пять, и разнобой между
        /// ними виден в сумме, а не поодиночке.</summary>
        public static float Tight => LvnTheme.Current.Tight;

        /// <summary>Обычная цель под палец (48). Правило среды, а не вкус:
        /// палец закрывает то, во что целится, и промах ощущается как
        /// поломка. Ставилось числом в четырёх написаниях.</summary>
        public static float Touch => LvnTheme.Current.Touch;

        /// <summary>Крупная цель (56): главное действие экрана, ряд
        /// меню — то, во что целятся не глядя.</summary>
        public static float TouchLg => LvnTheme.Current.TouchLg;

        public static float Space1 => LvnTheme.Current.Space1;
        public static float Space2 => LvnTheme.Current.Space2;
        public static float Space3 => LvnTheme.Current.Space3;
        public static float Space4 => LvnTheme.Current.Space4;
        public static float Space5 => LvnTheme.Current.Space5;
        public static float Space6 => LvnTheme.Current.Space6;

        public static float ButtonLift  => LvnTheme.Current.ButtonLift;
        public static Color ButtonShade => LvnTheme.Current.ButtonShade;
    }
}
