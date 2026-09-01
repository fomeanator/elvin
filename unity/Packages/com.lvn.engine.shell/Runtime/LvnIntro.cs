using System;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВВОДНАЯ — воронка первого запуска: новелла, которую играют ДО витрины и
    /// ровно один раз.
    ///
    /// <para>Знание о ней было размазано по четырём файлам. «Это вводная?» —
    /// сравнение поля с литералом — стояло ПЯТЬ раз. А «вводная пройдена»
    /// решали ДВОЕ и по разным правилам: цикл оболочки — по признаку
    /// «новелла пройдена», вход в главу — по финалу ПОСЛЕДНЕЙ ГЛАВЫ вводной.
    /// Второе правило заведено потому, что первое промахивалось на живом
    /// устройстве: партнёр получил «пролог по кругу» на чистой установке.
    /// Правило-заплатка встало рядом с прежним, а не вместо него.</para>
    ///
    /// <para>Свидетель теперь один — финал последней главы. Ворота при этом
    /// по-прежнему смотрят и на прогресс: у того, кто прошёл вводную ДО
    /// появления метки, её на устройстве нет, и без этой оговорки он увидел бы
    /// пролог заново.</para>
    /// </summary>
    public static class LvnIntro
    {
        /// <summary>Как новелла объявляет себя вводной в манифесте.</summary>
        public const string Type = "intro";

        /// <summary>Это вводная? Регистр не важен — поле авторское.</summary>
        public static bool Is(LvnTitle t)
            => t != null && string.Equals(t.type, Type, StringComparison.OrdinalIgnoreCase);

        /// <summary>Метка устройства: вводная уже пройдена.</summary>
        public static bool Done => LvnPrefs.IntroDone;

        /// <summary>
        /// ЕДИНСТВЕННЫЙ СВИДЕТЕЛЬ: вводная доиграна до конца. Зовут с финала её
        /// ПОСЛЕДНЕЙ главы — «дальше некуда». Бросил на середине — метки нет, и
        /// следующий запуск снова приведёт в воронку, а не на витрину, которую
        /// игрок ещё не заслужил.
        /// </summary>
        public static void NoteFinished(LvnTitle title)
        {
            if (!Is(title)) return;
            LvnPrefs.IntroDone = true;
            LvnLog.Trace("[lvn-intro] вводная доиграна до конца — витрина открыта");
        }

        /// <summary>
        /// ВОРОТА: какую вводную играть прямо сейчас, или null — пускать на
        /// витрину. Диагностический след здесь же: «почему не стартанула
        /// воронка» иначе выясняется раскопками в памяти чужого устройства.
        /// </summary>
        public static LvnTitle Pending(LvnManifest manifest)
        {
            if (Done)
            {
                LvnLog.Trace("[lvn-intro] ворота: IntroDone=true (метка устройства) — витрина");
                return null;
            }
            if (manifest?.titles == null) return null;
            foreach (var t in manifest.titles)
                if (Is(t))
                {
                    bool done = LvnProgress.Finished(t);
                    LvnLog.Trace($"[lvn-intro] ворота: '{t.id}' reached={LvnProgress.Reached(t)} "
                        + $"current={(LvnProgress.Current(t)?.id ?? "-")} → "
                        + (done ? "пройдена, витрина" : "играем воронку"));
                    return done ? null : t;
                }
            LvnLog.Trace("[lvn-intro] ворота: intro-тайтла в манифесте нет — витрина");
            return null;
        }
    }
}
