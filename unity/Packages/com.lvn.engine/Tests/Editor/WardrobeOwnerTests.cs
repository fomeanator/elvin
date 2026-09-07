using System.Collections.Generic;
using System.Reflection;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ГАРДЕРОБ ОДНОГО ИГРОКА НЕ ДОСТАЁТСЯ ДРУГОМУ.
    ///
    /// <para>Правило записано в <see cref="LvnKeep"/> прямым текстом: ключи без
    /// приставки принадлежат тому, кто вошёл здесь ПЕРВЫМ, а любой другой
    /// аккаунт получает собственное пространство через <c>Scoped</c>. Сейвы,
    /// прогресс, галерея и «прочитано» это правило соблюдают; гардероб не звал
    /// <c>Scoped</c> ни разу, и его ключи оставались голыми.</para>
    ///
    /// <para>Что это значит на общем телефоне: второй игрок открывает гардероб
    /// и видит наряд первого — включая то, за что не платил, — а переодевшись,
    /// затирает чужой. Плюс <c>Scoped</c> попутно вносит ключ в реестр личных
    /// данных, по которому исполняется «удалите меня»; голый ключ туда не
    /// попадал.</para>
    ///
    /// <para>Проверки для приёмки: снимите <c>Scoped</c> с ключей — упадут
    /// ВторойИгрокНеВидитНарядПервого и ВторойИгрокНеЗатираетНарядПервого;
    /// уберите сброс памяти при смене владельца — упадёт
    /// ПамятьНеОтдаётЧужойНарядПослеСменыВладельца.</para>
    /// </summary>
    public class WardrobeOwnerTests
    {
        private const string Hero = "test-wardrobe-hero";
        private const string First = "test-wardrobe-owner-a";
        private const string Second = "test-wardrobe-owner-b";
        private static readonly FieldInfo KnownKeys = typeof(LvnKeep).GetField(
            "_known", BindingFlags.Static | BindingFlags.NonPublic);

        private readonly Dictionary<string, string> _prefs = new Dictionary<string, string>();
        private string _previousOwner;
        private object _previousKnownKeys;

        [SetUp]
        public void SetUp()
        {
            _prefs.Clear();
            _previousOwner = LvnKeep.Owner;
            _previousKnownKeys = KnownKeys.GetValue(null);
            KeepAndClear("lvn.local.owner");
            KeepAndClear("lvn.local.keys");
            // Уборка идёт по ИМЕНАМ, независимо от реализации ключа: сломанная
            // реализация не должна оставить после себя тестовых нарядов.
            foreach (var prefix in new[] { "lvn_wardrobe_", "lvn_wardrobe_seen_" })
            {
                KeepAndClear(prefix + Hero);
                KeepAndClear(prefix + Second + "." + Hero);
            }
            KnownKeys.SetValue(null, null);
            LvnKeep.NoteOwner(First);
            LvnWardrobe.Clear(Hero);
        }

        [TearDown]
        public void TearDown()
        {
            LvnWardrobe.Clear(Hero);
            LvnKeep.NoteOwner(_previousOwner);
            using (LvnKeep.Batch())
                foreach (var pref in _prefs)
                    if (pref.Value == null) LvnKeep.Drop(pref.Key);
                    else LvnKeep.Put(pref.Key, pref.Value);
            KnownKeys.SetValue(null, _previousKnownKeys);
        }

        private void KeepAndClear(string key)
        {
            if (_prefs.ContainsKey(key)) return;
            _prefs.Add(key, LvnKeep.Has(key) ? LvnKeep.Get(key) : null);
            LvnKeep.Drop(key);
        }

        // Смена аккаунта в жизни не перезапускает игру, поэтому и здесь не
        // перезапускаем: переключаем владельца так же, как это делает вход.
        private static void SwitchTo(string owner) => LvnKeep.NoteOwner(owner);

        [Test]
        public void ВторойИгрокНеВидитНарядПервого()
        {
            LvnWardrobe.Equip(Hero, "dress", "gold");
            Assert.AreEqual("gold", LvnWardrobe.Equipped(Hero)["dress"], "первый оделся");

            SwitchTo(Second);
            Assert.IsFalse(LvnWardrobe.Equipped(Hero).ContainsKey("dress"),
                "второму игроку достался наряд первого — включая то, за что он не платил");
        }

        [Test]
        public void ВторойИгрокНеЗатираетНарядПервого()
        {
            LvnWardrobe.Equip(Hero, "dress", "gold");
            SwitchTo(Second);
            LvnWardrobe.Equip(Hero, "dress", "rags");

            SwitchTo(First);
            Assert.AreEqual("gold", LvnWardrobe.Equipped(Hero)["dress"],
                "первый вернулся и не нашёл своего наряда — его затёр второй");
        }

        [Test]
        public void ПамятьНеОтдаётЧужойНарядПослеСменыВладельца()
        {
            LvnWardrobe.Equip(Hero, "hat", "crown");
            // Читаем ПЕРЕД сменой, чтобы наряд лёг в память процесса.
            Assert.AreEqual("crown", LvnWardrobe.Equipped(Hero)["hat"]);

            SwitchTo(Second);
            Assert.IsFalse(LvnWardrobe.Equipped(Hero).ContainsKey("hat"),
                "ключи развели по владельцам, а память процесса всё ещё отдаёт чужое");
        }

        [Test]
        public void УвиденноеНеПереходитКДругомуИгроку()
        {
            LvnWardrobe.MarkSeen(Hero, "dress", "gold");
            Assert.IsTrue(LvnWardrobe.IsSeen(Hero, "dress", "gold"));

            SwitchTo(Second);
            Assert.IsFalse(LvnWardrobe.IsSeen(Hero, "dress", "gold"),
                "второму игроку новинка показана уже виденной");
        }

        [Test]
        public void УПервогоВладельцаКлючиНеМеняются()
        {
            // Байтовая совместимость: у того, кто вошёл первым, ключ прежний,
            // и ни один существующий гардероб не переезжает и не теряется.
            LvnWardrobe.Equip(Hero, "dress", "gold");
            Assert.IsTrue(LvnKeep.Has("lvn_wardrobe_" + Hero),
                "у первого владельца ключ обязан остаться прежним — иначе наряды пропадут при обновлении");
        }

        [Test]
        public void ЛичныеКлючиПопадаютВРеестрЗабвения()
        {
            LvnWardrobe.Equip(Hero, "dress", "gold");
            LvnWardrobe.MarkSeen(Hero, "dress", "gold");
            var реестр = LvnKeep.Get("lvn.local.keys", "");
            Assert.IsTrue(реестр.Contains("lvn_wardrobe_" + Hero),
                "ключ наряда не попал в реестр личных данных — «удалите меня» его не найдёт");
            Assert.IsTrue(реестр.Contains("lvn_wardrobe_seen_" + Hero),
                "ключ увиденного не попал в реестр личных данных");
        }
    }
}
