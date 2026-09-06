using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// НОВАЯ КОМАНДА НЕ ОКАЖЕТСЯ ВНЕ ПЕРЕСТРОЙКИ КАДРА МОЛЧА.
    ///
    /// <para>За одну ночь нашлись четыре дыры одного вида: полотно строилось
    /// по многу раз, объекты тоже (но лишь без следа), надписи и деревья
    /// схлопывались неверным ключом, а катсцена не восстанавливалась вовсе —
    /// замер дал ноль её команд в возвращённом кадре, хотя она прячет реплику,
    /// выборы и меню.</para>
    ///
    /// <para>Причина у всех одна и она не в коде, а в УМОЛЧАНИИ: уплотнение
    /// росло по предмету за раз, и каждый новый вид команды по умолчанию
    /// оказывался вне его. Пятая дыра появилась бы так же.</para>
    ///
    /// <para>Этот страж переворачивает умолчание: сценическая команда обязана
    /// быть названа в таблице классов. Отнести её можно и к «разовым», и к
    /// «вне кадра» — но СКАЗАВ это.</para>
    /// </summary>
    public class ReplayClassTests
    {
        private static HashSet<string> StageOps()
        {
            // Список сценических операций движка живёт приватным полем рядом с
            // диспетчером — берём его отражением, чтобы не заводить вторую копию:
            // две копии одного списка расходятся не «когда-нибудь», а при
            // следующей команде, которую впишут в одну из них.
            var f = typeof(LvnPlayer).GetField("_engineStageOps",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(f, "список сценических операций переехал — страж потерял опору");
            return new HashSet<string>((HashSet<string>)f.GetValue(null));
        }

        [Test]
        public void КаждаяСценическаяКомандаОтнесенаККлассу()
        {
            var stage = StageOps();
            var classified = new HashSet<string>(LvnPlayer.ReplayClasses.Keys);
            var missing = stage.Except(classified).OrderBy(x => x).ToList();

            Assert.IsEmpty(missing,
                "эти сценические команды не отнесены ни к одному классу перестройки кадра: "
              + string.Join(", ", missing)
              + ".\nОтнесите каждую в LvnPlayer.ReplayClasses — можно и к OneShot или Outside, "
              + "но СКАЗАВ это. Молчаливое умолчание уже стоило четырёх дыр за одну ночь: "
              + "полотно, объекты, надписи с деревьями, катсцена.");
        }

        [Test]
        public void ВТаблицеНетВыдуманныхКоманд()
        {
            var stage = StageOps();
            var extra = LvnPlayer.ReplayClasses.Keys.Except(stage).OrderBy(x => x).ToList();
            Assert.IsEmpty(extra,
                "в таблице классов есть команды, которых движок не ставит на сцену: "
              + string.Join(", ", extra) + " — таблица отстала от языка");
        }

        // Классы — не украшение: то, что названо состоянием, обязано
        // восстанавливаться, а не пропадать при возврате.
        [Test]
        public void СостоянияДействительноПопадаютВСлед()
        {
            foreach (var kv in LvnPlayer.ReplayClasses.Where(k => k.Value == LvnPlayer.ReplayClass.State))
            {
                var cmd = new Newtonsoft.Json.Linq.JObject { ["op"] = kv.Key };
                if (kv.Key == "camera") cmd["action"] = "zoom";
                if (kv.Key == "audio") cmd["channel"] = "music";
                var m = typeof(LvnPlayer).GetMethod("IsReplayedOp",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(m, "признак «попадает в след» переехал");
                Assert.IsTrue((bool)m.Invoke(null, new object[] { cmd }),
                    $"«{kv.Key}» назван состоянием кадра, но в след не попадает — "
                  + "значит при возврате его не будет, как было с катсценой");
            }
        }
    }
}
