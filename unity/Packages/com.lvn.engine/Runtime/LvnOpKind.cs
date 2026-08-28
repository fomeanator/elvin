namespace Lvn
{
    /// <summary>Чем занята команда: кем-то на сцене, фоном, вуалью или своим делом.</summary>
    public enum LvnOpSubject
    {
        /// <summary>Ни к чему из перечисленного — команда сама себе предмет.</summary>
        Other,
        /// <summary>Кто-то на сцене: актёр, предмет, их грим.</summary>
        Actor,
        /// <summary>Задник — плоский или трёхмерный.</summary>
        Background,
        /// <summary>Пелена поверх кадра: затемнение, вспышка, тон, размытие, эффекты.</summary>
        Veil,
    }

    /// <summary>
    /// К ЧЕМУ ОТНОСИТСЯ КОМАНДА — один ответ вместо перечислений по месту.
    ///
    /// <para>Знание «эти шесть операций — про вуаль, эти две — про фон» лежало
    /// в двух местах сразу: Рамка (что запоминать в кадре) и Распорядитель
    /// сцены (за какой предмет спорят отправители). Вопросы у них разные, а
    /// список — один и тот же, и списки уже начали расходиться.</para>
    ///
    /// <para>Цена расхождения тихая и отложенная: заводят новый эффект кадра,
    /// вносят его в один список — и он либо не попадает в кадр (не вернётся
    /// после «увести и вернуть»), либо не объединяется с прочими вуалями в
    /// споре, то есть перестаёт вытеснять предыдущую пелену. Ни то ни другое не
    /// выглядит как ошибка в новой команде — выглядит как «иногда мигает».</para>
    ///
    /// <para>НЕ путать с ключом трассы реплея (<c>LvnPlayer.TraceKey</c>): там
    /// нарочно другое деление — каждая вуаль отвечает сама за себя, потому что
    /// схлопывание трассы обязано совпадать с тем, как её потом переигрывают.
    /// Это другой вопрос, и сводить их нельзя.</para>
    /// </summary>
    public static class LvnOpKind
    {
        public static LvnOpSubject Of(string op)
        {
            switch (op)
            {
                case "actor":
                case "obj":
                case "sfx":
                    return LvnOpSubject.Actor;
                case "bg":
                case "bg3d":
                    return LvnOpSubject.Background;
                case "fade":
                case "dim":
                case "flash":
                case "tint":
                case "blur":
                case "fx":
                    return LvnOpSubject.Veil;
                default:
                    return LvnOpSubject.Other;
            }
        }

        /// <summary>Пелена поверх кадра — одна на всех, поэтому новая вытесняет
        /// прежнюю.</summary>
        public static bool IsVeil(string op) => Of(op) == LvnOpSubject.Veil;

        /// <summary>Задник, плоский или трёхмерный.</summary>
        public static bool IsBackground(string op) => Of(op) == LvnOpSubject.Background;
    }
}
