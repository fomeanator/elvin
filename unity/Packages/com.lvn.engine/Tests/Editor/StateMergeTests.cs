using Lvn;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    // Field-level stat merge: on a sync conflict, the other device's doc wins by
    // default and only the keys THIS device changed since the last agreed sync
    // overlay it — so two devices touching different stats both keep progress.
    public class StateMergeTests
    {
        private static JObject J(string json) => JObject.Parse(json);

        [Test]
        public void DevicesTouchingDifferentKeysBothKeepProgress()
        {
            var baseline = J(@"{""gold"":10,""bond.mara"":1}");
            var local    = J(@"{""gold"":10,""bond.mara"":5}");  // this device raised the bond
            var server   = J(@"{""gold"":99,""bond.mara"":1}");  // the other device earned gold

            var merged = HttpStateStore.MergeVars(server, local, baseline);

            Assert.AreEqual(99, (int)merged["gold"], "the other device's gold survives");
            Assert.AreEqual(5, (int)merged["bond.mara"], "this device's bond survives");
        }

        [Test]
        public void SameKeyConflictLocalChangeWins()
        {
            var baseline = J(@"{""route"":""a""}");
            var local    = J(@"{""route"":""b""}");
            var server   = J(@"{""route"":""c""}");
            var merged = HttpStateStore.MergeVars(server, local, baseline);
            Assert.AreEqual("b", (string)merged["route"],
                "a key changed on BOTH sides keeps this device's value (it retried the PUT)");
        }

        [Test]
        public void NewKeysFromBothSidesAreKept()
        {
            var baseline = J(@"{}");
            var local    = J(@"{""seen_intro"":true}");
            var server   = J(@"{""seen_credits"":true}");
            var merged = HttpStateStore.MergeVars(server, local, baseline);
            Assert.IsTrue((bool)merged["seen_intro"]);
            Assert.IsTrue((bool)merged["seen_credits"]);
        }

        [Test]
        public void NoBaselineFallsBackToOverlayAll()
        {
            var local  = J(@"{""gold"":5}");
            var server = J(@"{""gold"":99,""extra"":1}");
            var merged = HttpStateStore.MergeVars(server, local, null);
            Assert.AreEqual(5, (int)merged["gold"], "without a baseline every local key overlays (legacy behaviour)");
            Assert.AreEqual(1, (int)merged["extra"], "server-only keys survive");
        }

        [Test]
        public void NullSidesDegradeGracefully()
        {
            Assert.AreEqual(0, HttpStateStore.MergeVars(null, null, null).Count);
            var onlyLocal = HttpStateStore.MergeVars(null, J(@"{""a"":1}"), null);
            Assert.AreEqual(1, (int)onlyLocal["a"]);
        }
        // ── Круг: записали — прочитали — то же самое ─────────────────────────
        //
        // Слияние проверено выше, а САМА ЗАПИСЬ и чтение — нет. Пара половин
        // одного факта: разойдись они в мелочи (кириллица, вложенность, ноль
        // против отсутствия), и потеря будет молчаливой — прогресс игрока
        // просто станет другим, без единой строки в логе.

        private const string КругТитул = "t_roundtrip_probe";

        [TearDown]
        public void УбратьПробу()
        {
            LvnKeep.Drop(LocalStateStore.Key(КругТитул));
            LvnKeep.Drop(LocalStateStore.BaseKey(КругТитул));
        }

        [Test]
        public void ЗаписанноеЧитаетсяОбратноБезПотерь()
        {
            var doc = new JObject
            {
                ["число"] = 42,
                ["дробь"] = 0.5,
                ["ложь"] = false,
                ["пусто"] = "",
                ["строка"] = "Виктория «в кавычках» и \\ слэш",
                ["вложенное"] = new JObject { ["Way"] = new JObject { ["Moral"] = 3 } },
            };
            LocalStateStore.WriteDoc(КругТитул, doc);
            var назад = LocalStateStore.ReadDoc(КругТитул);

            Assert.NotNull(назад, "записанное не прочиталось вовсе");
            Assert.IsTrue(JToken.DeepEquals(doc, назад),
                $"круг не сошёлся:\n  было {doc.ToString(Newtonsoft.Json.Formatting.None)}" +
                $"\n  стало {назад.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        [Test]
        public void ОснованиеСинхронизацииХодитТемЖеКругом()
        {
            // База сравнения живёт по своему ключу, и путать её с документом
            // нельзя: на ней стоит ответ «что менял ИМЕННО ЭТОТ прибор».
            var b = new JObject { ["Relationships"] = new JObject { ["Anna"] = 2 } };
            LocalStateStore.WriteBase(КругТитул, b);
            Assert.IsTrue(JToken.DeepEquals(b, LocalStateStore.ReadBase(КругТитул)));
            Assert.IsNull(LocalStateStore.ReadDoc(КругТитул),
                "база записалась в документ — ключи перепутаны, и слияние будет сравнивать себя с собой");
        }

        [Test]
        public void НезаписанноеЧитаетсяКакНичего()
        {
            // «Ничего» и «пустой объект» — разные ответы: на первом слияние
            // берёт всё с сервера, на втором считает, что прибор всё стёр.
            Assert.IsNull(LocalStateStore.ReadDoc("t_never_written_probe"));
        }
    }
}
