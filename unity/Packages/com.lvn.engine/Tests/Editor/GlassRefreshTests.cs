using Lvn.UI.World;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// РАСХОД СТЕКЛА. Размытая подложка окна диалога считалась каждый кадр:
    /// уменьшение, четыре прохода размытия, переворот и проброс кадра — семь
    /// проходов, и так почти всю игру, потому что окно висит почти всегда.
    /// Правило «пересчитывать не чаще, чем нужно глазу» закреплено здесь, а не
    /// спрятано в середине отрисовки.
    /// </summary>
    public class GlassRefreshTests
    {
        [Test]
        public void FirstFrameAlwaysBuildsTheCopy()
        {
            Assert.IsTrue(LvnGlass.ShouldRefresh(-1f, 0f, hasCopy: false),
                "копии ещё нет — считать обязаны, иначе стекло будет пустым");
            Assert.IsTrue(LvnGlass.ShouldRefresh(10f, 10f, hasCopy: false),
                "копия потеряна (смена разрешения) — тоже считаем");
        }

        [Test]
        public void FreshCopyIsReusedBetweenRefreshes()
        {
            float step = 1f / LvnGlass.RefreshHz;
            Assert.IsFalse(LvnGlass.ShouldRefresh(10f, 10f + step * 0.4f, hasCopy: true),
                "подложка ещё свежая — пересчёт здесь и есть постоянный расход");
            // Полтора срока, а не ровно один: `10f + 1f/15f` в float даёт
            // разницу чуть МЕНЬШЕ срока, и проверка ловила бы точность
            // сложения вместо правила.
            Assert.IsTrue(LvnGlass.ShouldRefresh(10f, 10f + step * 1.5f, hasCopy: true),
                "срок вышел — иначе стекло отстанет от сцены заметно для глаза");
        }
    }
}
