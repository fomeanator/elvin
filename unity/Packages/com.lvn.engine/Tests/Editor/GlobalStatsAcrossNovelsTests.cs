using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СТАТЫ, КОТОРЫЕ ИГРОК НЕСЁТ ИЗ ИСТОРИИ В ИСТОРИЮ.
    ///
    /// <para>Обещание: <c>global.*</c> живёт не в главе, а в отдельной области
    /// хранилища, общей для всех новелл. Правило хранителя статов сказано
    /// прямо: они НЕ ОТКАТЫВАЮТСЯ вместе с главой и не берутся из снимка
    /// сохранения — «там они такие, какими были в момент записи, а другая
    /// новелла могла сдвинуть их с тех пор».</para>
    ///
    /// <para>Здесь это правило проверяется замером, а не чтением: где оно
    /// применено — держит, где не применено — видно.</para>
    /// </summary>
    public class GlobalStatsAcrossNovelsTests
    {
        private sealed class FakeStore : ILvnStateStore
        {
            public readonly Dictionary<string, JObject> Blobs = new Dictionary<string, JObject>();

            public Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
                => Task.FromResult(Blobs.TryGetValue(titleId, out var v)
                    ? (JObject)v.DeepClone() : null);

            public Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
            {
                Blobs[titleId] = vars == null ? new JObject() : (JObject)vars.DeepClone();
                return Task.CompletedTask;
            }
        }

        private static FakeStore StoreWithRep(int rep)
        {
            var s = new FakeStore();
            s.Blobs[LvnGlobalStats.ScopeId] = new JObject { ["rep"] = rep };
            return s;
        }

        private static int RepIn(JObject vars)
            => (int)((JObject)vars[LvnGlobalStats.VarName])["rep"];

        // ГЛАВНОЕ ОБЕЩАНИЕ: что набрано в одной новелле, видно в другой.
        [Test]
        public void ДругаяНовеллаВидитНабранное()
        {
            var store = StoreWithRep(7);
            var freshChapterVars = new JObject();

            LvnGlobalStats.OverlayAsync(store, freshChapterVars).GetAwaiter().GetResult();

            Assert.AreEqual(7, RepIn(freshChapterVars),
                "новая глава не увидела статов, набранных в другой новелле");
        }

        // Пустые статы не накладываются: пустой объект затёр бы уже набранное и
        // превратил «ничего не набрал в этой сессии» в «потерял всё».
        [Test]
        public void ПустыеСтатыНеЗатираютНабранное()
        {
            var store = new FakeStore();
            store.Blobs[LvnGlobalStats.ScopeId] = new JObject();
            var vars = new JObject { [LvnGlobalStats.VarName] = new JObject { ["rep"] = 4 } };

            LvnGlobalStats.OverlayAsync(store, vars).GetAwaiter().GetResult();

            Assert.AreEqual(4, RepIn(vars), "пустой блоб затёр набранное");
        }

        // ЗАМЕР ТЕКУЩЕГО ПОВЕДЕНИЯ, А НЕ ЖЕЛАЕМОГО.
        //
        // Снимок несёт статы такими, какими они были в момент записи. Значит
        // любое восстановление снимка возвращает их назад — и если поверх не
        // наложить живые, глава продолжится со старыми значениями. Ровно этого
        // требует правило хранителя, и ровно это делает возобновление с
        // автосейва. Тест фиксирует сам факт, чтобы он не выглядел
        // неожиданностью там, где наложения нет.
        [Test]
        public void СнимокНесётСтарыеСтатыИВозвращаетИхПриВосстановлении()
        {
            var player = MiniPlayer();
            player.Vars[LvnGlobalStats.VarName] = new JObject { ["rep"] = 5 };
            var snap = player.Save();

            // Другая новелла подняла статы, пока этот снимок лежал.
            player.Vars[LvnGlobalStats.VarName] = new JObject { ["rep"] = 9 };

            player.Restore(snap);

            Assert.AreEqual(5, (int)((JObject)player.Vars[LvnGlobalStats.VarName])["rep"],
                "снимок перестал нести статы — правило «накладывать живые поверх» стало ненужным, "
                + "и места, где оно применено, надо пересмотреть");
        }

        // ЛЕКАРСТВО СУЩЕСТВУЕТ И РАБОТАЕТ. Наложение живых статов поверх
        // восстановленного набора возвращает то, что набрано в другой новелле.
        [Test]
        public void НаложениеЖивыхСтатовЛечитВосстановление()
        {
            var store = StoreWithRep(9);
            var player = MiniPlayer();
            player.Vars[LvnGlobalStats.VarName] = new JObject { ["rep"] = 5 };
            var snap = player.Save();
            player.Restore(snap);

            var vars = new JObject { [LvnGlobalStats.VarName] = (JObject)player.Vars[LvnGlobalStats.VarName] };
            LvnGlobalStats.OverlayAsync(store, vars).GetAwaiter().GetResult();

            Assert.AreEqual(9, RepIn(vars), "наложение не вернуло живые статы");
        }

        private sealed class NullStage : ILvnStage
        {
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command, LvnSender sender) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static LvnPlayer MiniPlayer()
            => new LvnPlayer(LvnDocument.Parse(
                @"{""script"":[{""op"":""say"",""text"":""a""},{""op"":""say"",""text"":""b""}]}"),
                new NullStage());
    }
}
