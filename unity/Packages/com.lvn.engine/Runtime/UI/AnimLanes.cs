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
