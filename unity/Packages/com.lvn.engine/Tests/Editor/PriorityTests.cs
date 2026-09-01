using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Lvn.Content;

namespace Lvn.Tests
{
    /// <summary>
    /// ЛЕСТНИЦА ПРИОРИТЕТОВ — <see cref="LvnPriority"/>.
    ///
    /// <para>Проверяется не «какое число у ступени», а ПОРЯДОК: очередь без
    /// порядка забивается, и первым не доезжает как раз то, чего игрок ждёт.
    /// Живой случай 01.09: прогрев каста встал в одну очередь с главой ноль, и
    /// агент вводной оказался за спиной у сотни картинок, которых никто не
    /// ждал.</para>
    /// </summary>
    public class PriorityTests
    {
        [Test]
        public void Первый_кадр_раньше_остального_в_главе()
        {
            Assert.Less((int)LvnPriority.OfChapterPart(new LvnPart("a", "sprite", 0, critical: true), current: true),
                        (int)LvnPriority.OfChapterPart(new LvnPart("b", "sprite", 0, critical: false), current: true),
                        "критичное рисует первый кадр — оно и едет первым");
        }

        [Test]
        public void Текущая_глава_раньше_следующей()
        {
            var now = LvnPriority.OfChapterPart(new LvnPart("a", "sprite", 0, critical: false), current: true);
            var next = LvnPriority.OfChapterPart(new LvnPart("b", "sprite", 0, critical: true), current: false);
            Assert.Less((int)now, (int)next,
                "даже некритичное ТЕКУЩЕЙ главы нужнее критичного следующей: игрок здесь, а не там");
        }

        [Test]
        public void Витрина_раньше_библиотеки_а_запасной_облик_последним()
        {
            Assert.Less((int)LvnPriority.OfClass(AssetClass.Cover), (int)LvnPriority.OfClass(AssetClass.Other),
                "обложку игрок увидит, выйдя в меню, — то есть в любую секунду");
            Assert.Greater((int)LvnPriority.OfClass(AssetClass.Actor), (int)LvnPriority.OfClass(AssetClass.Cover),
                "позы про запас — последняя ступень: сюжет их пока не просил");
        }

        [Test]
        public void Раскладка_по_ступеням_сохраняет_порядок_внутри()
        {
            var parts = new[]
            {
                new LvnPart("späte", "sprite", 0, critical: false),
                new LvnPart("первая", "sprite", 0, critical: true),
                new LvnPart("вторая", "sprite", 0, critical: true),
            };
            var order = LvnPriority
                .ByRung(parts, p => LvnPriority.OfChapterPart(p, current: true))
                .Select(p => p.Url).ToList();

            Assert.AreEqual(new List<string> { "первая", "вторая", "späte" }, order,
                "внутри ступени порядок НЕ выдумывается: автор перечислил ассеты так, как они нужны сцене");
        }

        [Test]
        public void Пустой_список_не_роняет()
            => Assert.IsEmpty(LvnPriority.ByRung(new LvnPart[0], p => LvnRung.Live).ToList());
    }
}
