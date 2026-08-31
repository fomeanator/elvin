using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СЛОВАРЬ ИМЕНОВАННЫХ МЕСТ — один на весь движок.
    ///
    /// <para>Раньше на вопрос «где стоит <c>center_left</c>» отвечали семь
    /// списков и четыре разных словаря, и они уже разошлись: движок знал
    /// <c>center_left</c>/<c>center_right</c>, но не знал <c>offscreen_left</c> —
    /// а его подсказывал редактор и принимал компилятор, и актёр, которого автор
    /// увёл ЗА кадр, вставал в ЦЕНТР экрана, поперёк сцены. Компиляторы, наоборот,
    /// не знали <c>center_left</c>: слово молча становилось ЭМОЦИЕЙ, и герой
    /// получал не место, а несуществующее выражение лица.</para>
    ///
    /// <para>Здесь закреплены ПРАВИЛА словаря, а не его числа поштучно:
    /// девять имён идут слева направо и ни одно не проваливается в центр по
    /// умолчанию; заэкранные доли лежат ВНЕ кадра; список мест, где можно
    /// СТОЯТЬ, — это тот же словарь без заэкранных, а не третья копия чисел;
    /// незнакомое слово и отсутствие слова дают центр; авторский слот из
    /// каталога сильнее словаря. И расталкивая толпу, сцена никого не может
    /// вытолкнуть из кадра.</para>
    /// </summary>
    public sealed class PlacementSlotsTests
    {
        private const float Eps = 0.0005f;

        /// <summary>Имена мест, где можно стоять, — словарь без заэкранных.</summary>
        private static readonly string[] СтоячиеИмена =
        {
            "far_left", "left", "center_left", "center", "center_right", "right", "far_right",
        };

        // ── словарь целиком ─────────────────────────────────────────────────

        [Test]
        public void ДевятьМестИдутСлеваНаправоИНиОдноНеПровалилосьВЦентр()
        {
            Assert.AreEqual(9, Placement.SlotNames.Length,
                "мест должно быть девять: семь на сцене плюс два за кадром");

            var предыдущее = float.NegativeInfinity;
            var виденные = new HashSet<float>();
            foreach (var имя in Placement.SlotNames)
            {
                var x = Placement.SlotX(имя);
                // Строгий рост — он же доказательство, что слово ЗНАКОМО:
                // незнакомое вернуло бы центр и порядок бы сломался.
                Assert.Greater(x, предыдущее,
                    $"место '{имя}' стоит не правее предыдущего — либо порядок словаря "
                    + "перепутан, либо слово выпало из switch и молча стало центром");
                Assert.IsTrue(виденные.Add(x),
                    $"место '{имя}' совпало с другим — у имени нет своего места");
                предыдущее = x;
            }

            Assert.AreEqual("offscreen_left", Placement.SlotNames[0],
                "крайнее левое имя — заэкранное; порядок словаря читают сторожа");
            Assert.AreEqual("offscreen_right", Placement.SlotNames[Placement.SlotNames.Length - 1],
                "крайнее правое имя — заэкранное");
            Assert.AreEqual(0.5f, Placement.SlotX("center"), Eps, "центр — середина кадра");
        }

        // ── за кадром ───────────────────────────────────────────────────────

        [Test]
        public void ЗаэкранныеМестаЛежатВнеКадраАНеЛипнутККраю()
        {
            // Доля НАМЕРЕННО вне [0,1]: `position=offscreen_left` — это уход
            // ЗА кулисы. Зажатая в 0 фигура прилипает к краю и остаётся в кадре
            // приклеенным к рамке силуэтом — игрок видит героя, который «ушёл».
            Assert.Less(Placement.SlotX("offscreen_left"), 0f,
                "offscreen_left в кадре — актёр, уведённый за кулисы, липнет к левому краю");
            Assert.Greater(Placement.SlotX("offscreen_right"), 1f,
                "offscreen_right в кадре — актёр, уведённый за кулисы, липнет к правому краю");

            // …и симметрично: уход налево и направо стоят одинаково далеко.
            Assert.AreEqual(0f - Placement.SlotX("offscreen_left"),
                Placement.SlotX("offscreen_right") - 1f, Eps,
                "уходы влево и вправо несимметричны — один герой уйдёт дальше другого");
        }

        // ── где можно СТОЯТЬ ────────────────────────────────────────────────

        [Test]
        public void СтоячиеМестаЭтоТотЖеСловарьБезЗаэкранных()
        {
            Assert.AreEqual(Placement.SlotNames.Length - 2, Placement.StandingSlotXs.Length,
                "стоячих мест не семь — список разошёлся со словарём");

            for (int i = 0; i < СтоячиеИмена.Length; i++)
                Assert.AreEqual(Placement.SlotX(СтоячиеИмена[i]), Placement.StandingSlotXs[i], Eps,
                    $"стоячее место #{i} не совпало с '{СтоячиеИмена[i]}' из словаря — "
                    + "числа выписаны второй раз руками и уже разъехались");
        }

        [Test]
        public void СредиСтоячихМестНетЗаэкранных()
        {
            // Расталкивая толпу, сцена выбирает из ЭТОГО списка. Попади сюда
            // заэкранная доля — арбитр «решил бы» коллизию, выкинув актёра
            // из кадра: игрок увидел бы, как персонаж исчезает без причины.
            // Заэкранные доли лежат вне [0,1] (см. соседний тест), поэтому
            // «внутри кадра» — это и есть «не заэкранное».
            foreach (var x in Placement.StandingSlotXs)
            {
                Assert.Greater(x, 0f, "стоячее место за левым краем кадра — заэкранная доля "
                    + "попала в список, из которого арбитр расселяет толпу");
                Assert.Less(x, 1f, "стоячее место за правым краем кадра — заэкранная доля "
                    + "попала в список, из которого арбитр расселяет толпу");
            }
        }

        [Test]
        public void СтоячиеМестаИдутПоВозрастанию()
        {
            var предыдущее = float.NegativeInfinity;
            foreach (var x in Placement.StandingSlotXs)
            {
                Assert.Greater(x, предыдущее,
                    "стоячие места не по возрастанию — арбитр ищет ближайшее свободное "
                    + "и на неупорядоченном списке разведёт толпу не в ту сторону");
                предыдущее = x;
            }
        }

        // ── чего словарь не знает ───────────────────────────────────────────

        [Test]
        public void НезнакомоеСловоИПустоеМестоДаютЦентр()
        {
            Assert.AreEqual(0.5f, Placement.SlotX("porch"), Eps,
                "незнакомое слово должно давать центр, а не бросать исключение");
            Assert.AreEqual(0.5f, Placement.SlotX(null), Eps,
                "команда без position= — обычное дело: актёр встаёт в центр");
            Assert.AreEqual(0.5f, Placement.SlotX(""), Eps);
            Assert.AreEqual(0.5f, Placement.SlotX("Left"), Eps,
                "словарь регистрозависим — слово с большой буквы это НЕ 'left'");
        }

        // ── авторский слот из каталога ──────────────────────────────────────

        [Test]
        public void АвторскийСлотИзКаталогаСильнееСловаря()
        {
            // Персонаж может стоять по-своему: у одного «left» — это его порог,
            // у другого — общий столбец. Каталог сущности перебивает словарь.
            var свои = new Dictionary<string, float> { ["left"] = 0.05f };
            Assert.AreEqual(0.05f, VnStage.SlotXFor("left", свои), Eps,
                "слот из каталога сущности проигрывает общему словарю");
        }

        [Test]
        public void КаталогМожетЗавестиСвоёМестоКоторогоВСловареНет()
        {
            var свои = new Dictionary<string, float> { ["porch"] = 0.97f };
            Assert.AreEqual(0.97f, VnStage.SlotXFor("porch", свои), Eps,
                "имя места, известное только каталогу, молча стало центром");
        }

        [Test]
        public void БезСвоегоСлотаРаботаетОбщийСловарь()
        {
            var свои = new Dictionary<string, float> { ["left"] = 0.05f };
            Assert.AreEqual(Placement.SlotX("right"), VnStage.SlotXFor("right", свои), Eps,
                "каталог перебил не своё имя");
            Assert.AreEqual(Placement.SlotX("right"), VnStage.SlotXFor("right", null), Eps,
                "без каталога — общий словарь");
            Assert.AreEqual(0.5f, VnStage.SlotXFor(null, свои), Eps,
                "команда без position= с каталогом на руках — всё равно центр");
        }

        // ── расталкивание толпы ─────────────────────────────────────────────

        [Test]
        public void РасталкиваяТолпуНикогоНеВыталкиваетИзКадра()
        {
            // По одному занятому месту за раз: куда бы арбитр ни сдвинул
            // претендента, это место В КАДРЕ. Иначе «двое встали друг в друга»
            // лечилось бы исчезновением одного из них.
            foreach (var занято in Placement.StandingSlotXs)
            {
                var другие = new[] { Актёр("roman", занято) };
                var x = VnStage.ArbitrateSlotX(занято, "miron", false, другие, null, out var хозяин);
                Assert.AreEqual("roman", хозяин, $"коллизия на {занято:0.00} не замечена");
                Assert.Greater(x, 0f, $"с занятого {занято:0.00} претендента вынесло за левый край");
                Assert.Less(x, 1f, $"с занятого {занято:0.00} претендента вынесло за правый край");
                Assert.Contains(x, Placement.StandingSlotXs,
                    "сдвиг не в стоячее место — арбитр выбирает не из словаря");
            }
        }

        [Test]
        public void КрайнееЛевоеМестоРасталкиваетсяВнутрьКадраАНеЗаКулисы()
        {
            // У far_left нет соседа слева СРЕДИ СТОЯЧИХ — и это правило:
            // ближайшая свободная точка слева (offscreen_left) кандидатом
            // быть не должна.
            var другие = new[] { Актёр("roman", Placement.SlotX("far_left")) };
            var x = VnStage.ArbitrateSlotX(Placement.SlotX("far_left"), "miron", false,
                другие, null, out _);
            Assert.AreEqual(Placement.SlotX("left"), x, Eps,
                "занятое far_left увело претендента не в 'left' — проверь, не попало ли "
                + "заэкранное место в кандидаты");
        }

        [Test]
        public void ПолностьюЗанятаяСценаВсёРавноОставляетПретендентаВКадре()
        {
            var толпа = new List<KeyValuePair<string, Placement>>();
            for (int i = 0; i < Placement.StandingSlotXs.Length; i++)
                толпа.Add(Актёр("a" + i, Placement.StandingSlotXs[i]));

            foreach (var желаемое in Placement.StandingSlotXs)
            {
                var x = VnStage.ArbitrateSlotX(желаемое, "miron", false, толпа, null, out var хозяин);
                Assert.IsNotNull(хозяин);
                Assert.GreaterOrEqual(x, 0.05f, "в полной толпе претендента вынесло за левый край");
                Assert.LessOrEqual(x, 0.95f, "в полной толпе претендента вынесло за правый край");
            }
        }

        private static KeyValuePair<string, Placement> Актёр(string id, float x, bool показан = true)
            => new KeyValuePair<string, Placement>(id, new Placement { X = x, Show = показан });
    }
}
