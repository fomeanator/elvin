using System;
using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СТУПЕНИ РУЧЕК ИГРОКА: ПРЕДЕЛ ВЫВОДИТСЯ ИЗ СТУПЕНЕЙ, а не пишется рядом.
    ///
    /// <para>Живой дефект, который держат эти проверки: пока предел был
    /// ОТДЕЛЬНЫМ числом, ступени и зажим разошлись — настройка размера реплик
    /// обещала верхнюю границу 1,4, а ступеней выше 1,3 не существовало ни на
    /// одном экране. Четверть обещанного диапазона была недостижима, и понять
    /// это по коду было нельзя: оба числа выглядели правильными по
    /// отдельности.</para>
    ///
    /// <para>Поэтому здесь почти нет литералов: проверяется не «потолок равен
    /// 1,3», а «потолок — это последняя ступень, которую экран действительно
    /// предлагает». Ряд ступеней можно менять свободно; разойтись с зажимом он
    /// больше не может.</para>
    /// </summary>
    public sealed class KnobsTests
    {
        [TearDown]
        public void Reset()
        {
            // Настройки статические и текут между тестами — возвращаем «как
            // нарисовано».
            LvnPrefs.TextScale = 1f;
            LvnPrefs.UiScale = 1f;
        }

        [Test]
        public void ПределЭтоКрайниеСтупениАНеОтдельныеЧисла()
        {
            var steps = LvnKnobs.Scale;

            Assert.AreEqual(steps[0].K, LvnKnobs.ScaleMin, 0.0001f,
                "нижняя граница разошлась с первой ступенью — ступень ниже предела недостижима");
            Assert.AreEqual(steps[steps.Length - 1].K, LvnKnobs.ScaleMax, 0.0001f,
                "верхняя граница разошлась с последней ступенью — ровно этот баг и был: " +
                "зажим обещал 1,4, а ступеней выше 1,3 ни один экран не предлагал");
        }

        [Test]
        public void ЗажимНеВыпускаетЗаКрайниеСтупени()
        {
            Assert.AreEqual(LvnKnobs.ScaleMin, LvnKnobs.ClampScale(-5f), 0.0001f);
            Assert.AreEqual(LvnKnobs.ScaleMin, LvnKnobs.ClampScale(LvnKnobs.ScaleMin - 0.1f), 0.0001f);
            Assert.AreEqual(LvnKnobs.ScaleMax, LvnKnobs.ClampScale(99f), 0.0001f);
            Assert.AreEqual(LvnKnobs.ScaleMax, LvnKnobs.ClampScale(LvnKnobs.ScaleMax + 0.1f), 0.0001f);
        }

        [Test]
        public void ЗажимНеТрогаетТоЧтоВнутриДиапазона()
        {
            foreach (var s in LvnKnobs.Scale)
                Assert.AreEqual(s.K, LvnKnobs.ClampScale(s.K), 0.0001f,
                    "предлагаемая ступень не пережила собственный зажим");
        }

        [Test]
        public void СтупеньУзнаётсяСДопускомИНеПутаетсяССоседней()
        {
            var steps = LvnKnobs.Scale;
            for (int i = 0; i < steps.Length; i++)
            {
                Assert.IsTrue(LvnKnobs.At(steps[i].K, steps[i]),
                    $"ступень «{steps[i].Key}» не узнала сама себя");
                // Допуск нужен потому, что значение возвращается из настроек
                // через запись и чтение float: сравнивать доли на равенство
                // нельзя, иначе подсветка текущей ступени гаснет ни с того ни с сего.
                Assert.IsTrue(LvnKnobs.At(steps[i].K + 0.004f, steps[i]),
                    $"ступень «{steps[i].Key}» не пережила запись и чтение float");
                Assert.IsTrue(LvnKnobs.At(steps[i].K - 0.004f, steps[i]));

                if (i + 1 < steps.Length)
                {
                    Assert.IsFalse(LvnKnobs.At(steps[i].K, steps[i + 1]),
                        "соседняя ступень подсветилась как текущая — игрок видит две выбранные сразу");
                    Assert.IsFalse(LvnKnobs.At(steps[i + 1].K, steps[i]));
                }
            }
        }

        [Test]
        public void СтупениИдутПоВозрастаниюИБезПовторов()
        {
            var steps = LvnKnobs.Scale;
            Assert.Greater(steps.Length, 1, "ручка из одной ступени — не ручка");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < steps.Length; i++)
            {
                if (i > 0)
                    Assert.Greater(steps[i].K, steps[i - 1].K,
                        "ряд ступеней не по возрастанию — крайние перестают быть границами, " +
                        "и ScaleMin/ScaleMax начинают врать");
                Assert.IsTrue(keys.Add(steps[i].Key),
                    $"ключ слова «{steps[i].Key}» повторяется — две ступени подпишутся одинаково");
                Assert.IsNotEmpty(steps[i].En, "у ступени нет английского умолчания");
            }
        }

        [Test]
        public void РазмерРепликЗажатРовноПоСтупеням()
        {
            // Живой дефект: настройка размера реплик обещала потолок 1,4 —
            // числа, которого ни один экран не предлагал. Теперь потолок обязан
            // быть ступенью, до которой игрок может дотянуться пальцем.
            LvnPrefs.TextScale = 99f;
            Assert.AreEqual(LvnKnobs.ScaleMax, LvnPrefs.TextScale, 0.0001f,
                "текст обещает потолок, которого нет среди ступеней");
            Assert.IsTrue(IsOfferedStep(LvnPrefs.TextScale),
                "зажатый потолок — не ступень: экран не умеет его выбрать");

            LvnPrefs.TextScale = -1f;
            Assert.AreEqual(LvnKnobs.ScaleMin, LvnPrefs.TextScale, 0.0001f);
            Assert.IsTrue(IsOfferedStep(LvnPrefs.TextScale));
        }

        [Test]
        public void МасштабИнтерфейсаЗажатТемиЖеСтупенями()
        {
            // Две ручки — одни ступени: «размер» в меню главы и «размер» в
            // настройках оболочки это два экрана ОДНОЙ настройки, и разный
            // потолок у них читался бы как поломка одного из них.
            LvnPrefs.UiScale = 99f;
            LvnPrefs.TextScale = 99f;
            Assert.AreEqual(LvnPrefs.TextScale, LvnPrefs.UiScale, 0.0001f,
                "у двух экранов одной настройки разошёлся потолок");

            LvnPrefs.UiScale = -1f;
            LvnPrefs.TextScale = -1f;
            Assert.AreEqual(LvnPrefs.TextScale, LvnPrefs.UiScale, 0.0001f);
            Assert.AreEqual(LvnKnobs.ScaleMin, LvnPrefs.UiScale, 0.0001f);
        }

        private static bool IsOfferedStep(float v)
            => Array.Exists(LvnKnobs.Scale, s => LvnKnobs.At(v, s));
    }
}
