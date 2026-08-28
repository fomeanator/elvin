using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ШВЕЙЦАР — кто впускает игрока в меню.
    ///
    /// <para>Работа простая на словах и склонная разъезжаться в коде: увести
    /// поверхности за кромки, дождаться, когда их станет видно, показать экран и
    /// одновременно двинуть все полосы. Пока хозяина у неё не было, порядок
    /// шагов знал вызывающий цикл оболочки, длительности и предохранители жили
    /// копиями в хабе и в верхнем баре, а «когда показывать» решалось в третьем
    /// месте. Отсюда обе поломки 28.08: меню висело готовым до собственного
    /// входа (предохранитель одной поверхности срабатывал раньше движения), и
    /// вход отыгрывал под брендовой вуалью, то есть впустую.</para>
    ///
    /// <para>Разделение простое. ПОВЕРХНОСТЬ знает своё движение: откуда
    /// приезжает и по какой кривой (<see cref="ILvnEntrance"/>). ШВЕЙЦАР знает
    /// ПОРЯДОК и МОМЕНТ: заряжает всех разом, держит дверь, пока идёт загрузка,
    /// впускает одним движением и отвечает за то, чтобы никто не остался за
    /// кромкой, чем бы дело ни кончилось.</para>
    /// </summary>
    public interface ILvnEntrance
    {
        /// <summary>Уйти за кромку и ждать. Вызывается ДО показа экрана.</summary>
        void ArmEntrance();
        /// <summary>Сыграть вход с текущего места.</summary>
        void PlayEntrance();
        /// <summary>Встать на место немедленно — вход не состоялся.</summary>
        void RestoreEntrance();
    }

    public static class LvnUsher
    {
        /// <summary>Сколько Швейцар держит дверь, прежде чем впустить без
        /// церемонии. Предел про «загрузка зависла», а не про её нормальную
        /// длину: первый запуск идёт секунды, и угадывать их числом нельзя.</summary>
        public const float HoldLimitSeconds = 20f;

        /// <summary>Через сколько после начала движения поверхности ставятся на
        /// место принудительно. Считается ОТ ВХОДА, а не от заряда: между ними
        /// лежит загрузка, и предохранитель, заведённый раньше времени, сам
        /// выдавал готовое меню посреди ожидания.</summary>
        public static int FailsafeMs => Lvn.UI.LvnMotion.Ms(Lvn.UI.LvnMotion.Curtain) * 3;

        /// <summary>
        /// Впустить: зарядить поверхности, дождаться <paramref name="hold"/>,
        /// показать экран и сыграть вход одним движением.
        /// </summary>
        /// <param name="hold">Пока true — дверь закрыта (например, поверх ещё
        /// лежит брендовая вуаль). Null = впускать сразу.</param>
        /// <param name="show">Показать экран. Вызывается ПОСЛЕ ожидания и ДО
        /// движения — тогда увидеть меню «заранее готовым» физически негде.</param>
        public static async Task OpenAsync(Func<bool> hold, Action show,
                                           params ILvnEntrance[] surfaces)
        {
            Arm(surfaces);
            if (hold != null)
            {
                // Ждём кадрами, а не сном: чужой таймер (вуаль гаснет по
                // своему) и угаданные миллисекунды рано или поздно разойдутся.
                float until = Lvn.LvnClock.Wall() + HoldLimitSeconds;
                while (hold() && Lvn.LvnClock.Wall() < until)
                    await Task.Yield();
            }
            show?.Invoke();
            Play(surfaces);
        }

        /// <summary>Зарядить поверхности — увести за кромки до показа.</summary>
        public static void Arm(params ILvnEntrance[] surfaces)
        {
            if (surfaces == null) return;
            foreach (var s in surfaces) s?.ArmEntrance();
        }

        /// <summary>Сыграть вход и поставить общий предохранитель: сорванное
        /// движение (пересборка документа, смена темы) не должно оставить
        /// поверхность за кромкой.</summary>
        public static void Play(params ILvnEntrance[] surfaces)
        {
            if (surfaces == null) return;
            foreach (var s in surfaces) s?.PlayEntrance();
            LvnAsync.Fire(FailsafeAsync(surfaces), "UsherFailsafe");
        }

        private static async Task FailsafeAsync(ILvnEntrance[] surfaces)
        {
            await Task.Delay(FailsafeMs);
            foreach (var s in surfaces) s?.RestoreEntrance();
        }
    }
}
