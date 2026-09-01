using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// ДОРОЖКИ АНИМАЦИИ — общие правила для плоской сцены и трёхмерного мира.
    ///
    /// <para>Анимации идут по именованным дорожкам; те, что запустил сценарий,
    /// зовутся «script:&lt;цель&gt;». Остановка сценарных дорожек была написана
    /// дважды почти дословно (у плоского компоновщика и WorldActor) — разница лишь в
    /// том, что каждый зовёт СВОЙ способ вернуть цели в покой.</para>
    /// </summary>
    public static class AnimLanes
    {
        /// <summary>Префикс дорожек, запущенных оператором сценария.</summary>
        public const string ScriptPrefix = "script:";

        /// <summary>
        /// УБРАТЬ ОДНУ ДОРОЖКУ — вместе с её очередью.
        ///
        /// <para>Дорожка живёт в ДВУХ памятях: то, что играет сейчас, и то, что
        /// ждёт очереди. Убрать её — значит тронуть обе, и это писали по месту
        /// трижды: остановить дорожку, остановить всё, остановить цель. Каждое
        /// написание обязано было помнить про вторую память, и одно из них уже
        /// стояло в четыре строки подряд, потому что цель ищется под двумя
        /// именами.</para>
        ///
        /// <para>Дубля здесь не видно поиском: три места пишут РАЗНЫЕ строки
        /// про одно правило. Видно только вопрос — «а если памятей станет
        /// три».</para>
        /// </summary>
        /// <returns>true, если что-то действительно убрали.</returns>
        public static bool Drop<TActive, TQueued>(
            Dictionary<string, TActive> channels,
            Dictionary<string, TQueued> queued, string lane)
        {
            if (string.IsNullOrEmpty(lane)) return false;
            queued?.Remove(lane);
            return channels != null && channels.Remove(lane);
        }

        /// <summary>
        /// Убрать дорожку ЦЕЛИ — под обоими именами: как её назвали и как её
        /// зовёт сценарий (<c>script:цель</c>). Оператор говорит «останови
        /// поворот головы», не зная, кто её запустил.
        /// </summary>
        public static bool DropTarget<TActive, TQueued>(
            Dictionary<string, TActive> channels,
            Dictionary<string, TQueued> queued, string target)
        {
            bool a = Drop(channels, queued, target);
            bool b = Drop(channels, queued, ScriptPrefix + target);
            return a || b;
        }

        /// <summary>Убрать ВСЕ дорожки: обе памяти пусты.</summary>
        public static void DropAll<TActive, TQueued>(
            Dictionary<string, TActive> channels,
            Dictionary<string, TQueued> queued)
        {
            channels?.Clear();
            queued?.Clear();
        }

        /// <summary>
        /// Убрать все сценарные дорожки и их очереди.
        /// </summary>
        /// <returns>true, если дорожек не осталось вовсе — вызывающему пора
        /// вернуть цели в покой. Если что-то ещё играет, покой не наводят:
        /// следующий кадр пересоберёт картину из выживших дорожек.</returns>
        public static bool DropScript<TActive, TQueued>(
            Dictionary<string, TActive> channels,
            Dictionary<string, TQueued> queued)
        {
            List<string> doomed = null;
            if (channels != null)
                foreach (var k in channels.Keys)
                    if (k.StartsWith(ScriptPrefix))
                        (doomed ??= new List<string>()).Add(k);

            if (queued != null)
                foreach (var k in new List<string>(queued.Keys))
                    if (k.StartsWith(ScriptPrefix))
                        queued.Remove(k);

            if (doomed == null) return false;
            foreach (var k in doomed) channels.Remove(k);
            return channels.Count == 0;
        }
    }
}
