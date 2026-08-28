using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// РАЗМЫТИЕ ДВУМЯ ПРОХОДАМИ — горизонтальный, затем вертикальный, столько
    /// раз, сколько просят.
    ///
    /// <para>Гаусс раскладывается на две одномерные свёртки: так он стоит 2N
    /// выборок вместо N². Приём известный, и потому его писали на месте — три
    /// эффекта движка держали его своей копией: размытие фона (<see
    /// cref="LvnBlurEffect"/>), матовое стекло (<see cref="LvnGlass"/>) и блум
    /// в стопке эффектов (<see cref="LvnFxStack"/>). Отличались только числа:
    /// одна—три итерации против двух, радиус по силе против жёсткого.</para>
    ///
    /// <para>Копия здесь опаснее обычной. Ошибиться в направлении (<c>_Dir</c>)
    /// или перепутать порядок текстур в пинг-понге — это не падение и не
    /// красная строка в логе: кадр остаётся правдоподобным, просто размытие
    /// выходит полосой в одну сторону. На движущейся сцене такое замечают через
    /// недели, а на скриншоте не видно вовсе.</para>
    ///
    /// <para>РЕЗУЛЬТАТ ОСТАЁТСЯ В <paramref name="a"/>. Пинг-понг с чётным
    /// числом проходов возвращает картинку в исходную текстуру, и это часть
    /// договора: вызывающий забирает <paramref name="a"/>, а
    /// <paramref name="b"/> — черновик, который он же и освобождает.</para>
    /// </summary>
    internal static class LvnBlurPass
    {
        /// <summary>Имя прохода в шейдере <c>LvnBlur</c>: одномерная свёртка по
        /// направлению <c>_Dir</c>.</summary>
        public const int DirectionalPass = 0;

        /// <summary>
        /// Прогнать <paramref name="iterations"/> пар проходов Г→В между двумя
        /// текстурами. Материал должен нести шейдер с направленным проходом
        /// (<paramref name="pass"/>); радиус ставится один раз на все итерации.
        /// </summary>
        public static void Run(Material mat, RenderTexture a, RenderTexture b,
                               float radius, int iterations, int pass = DirectionalPass)
        {
            if (mat == null || a == null || b == null || iterations <= 0) return;
            mat.SetFloat("_Radius", radius);
            for (int i = 0; i < iterations; i++)
            {
                mat.SetVector("_Dir", new Vector4(1f, 0f, 0f, 0f));
                Graphics.Blit(a, b, mat, pass);
                mat.SetVector("_Dir", new Vector4(0f, 1f, 0f, 0f));
                Graphics.Blit(b, a, mat, pass);
            }
        }

        /// <summary>
        /// ОДНА пара проходов БЕЗ смены радиуса — для тех, кто уже настроил
        /// материал сам (блум ставит порог и свои текстуры до размытия).
        /// Результат, как и выше, остаётся в <paramref name="a"/>.
        /// </summary>
        public static void Once(Material mat, RenderTexture a, RenderTexture b, int pass)
        {
            if (mat == null || a == null || b == null) return;
            mat.SetVector("_Dir", new Vector4(1f, 0f, 0f, 0f));
            Graphics.Blit(a, b, mat, pass);
            mat.SetVector("_Dir", new Vector4(0f, 1f, 0f, 0f));
            Graphics.Blit(b, a, mat, pass);
        }
    }
}
