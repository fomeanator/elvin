using System;
using System.Collections;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ПРОМОТКА ПЕРЕЖИВАЕТ ПРОДОЛЖЕНИЕ ГЛАВЫ.
    ///
    /// <para>Промотка — передача для ПЕРЕЧИТЫВАНИЯ: игрок возвращается к
    /// развилке через три знакомые главы. Главы идут встык, и если промотка
    /// гаснет на каждой границе, человек обязан на каждой открыть меню и
    /// ткнуть «промотать» заново — три раза за один заход.</para>
    ///
    /// <para>Ровно это решение движок уже принял для ВЫБОРА: там промотка
    /// сбавляет ход ради развилки, но после осознанного тапа включается сама
    /// («no safer for a real player who just made the pick herself»). Граница
    /// главы — тот же случай: игрок ничего не решал, ему нечего было
    /// пропустить.</para>
    ///
    /// <para>Вторая половина обещания важнее первой: промотка НЕ должна
    /// воскресать там, где главу выбрал человек — из витрины или из
    /// сохранения. Обе стороны держатся в одном файле намеренно.</para>
    /// </summary>
    public class SkipAcrossChapterTests
    {
        private const string Новелла = "стенд-промотка-через-главу";

        private static string Глава(string сцена) => @"{""scene"":""" + сцена + @""",""script"":[
            {""op"":""say"",""text"":""знакомая реплика один""},
            {""op"":""say"",""text"":""знакомая реплика два""},
            {""op"":""say"",""text"":""знакомая реплика три""}]}";

        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            _stage = TestStage.Panel("skip-across-chapter", out _go, out _panel);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Уборка()
        {
            _stage?.StopSkip();
            _stage?.ClearStage();
            _stage = null;
            yield return null;
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            yield return null;
        }

        private IEnumerator Ждём(Func<bool> готово, float секунд)
        {
            float срок = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < срок && !готово()) yield return null;
        }

        /// <summary>Первая глава, промотанная ДО КОНЦА — так это и выглядит у
        /// перечитывающего: он не останавливает промотку, она сама упирается в
        /// конец главы.</summary>
        private IEnumerator ПерваяГлаваПромотанаДоКонца()
        {
            _stage.SetSaveContext(Новелла, "ch1", "/content/scripts/промотка-ch01.lvn");
            _stage.Play(Глава("первая"));
            yield return null;
            _stage.StartSkip();
            Assert.IsTrue(_stage.Skipping, "стенд: промотка не включилась — мерить нечего");

            yield return Ждём(() => _stage.Player != null && _stage.Player.Finished, 10f);
            Assert.IsTrue(_stage.Player != null && _stage.Player.Finished,
                "стенд не смог домотать главу промоткой — вердикт о том, что бывает ПОСЛЕ "
                + "неё, был бы выдумкой");
        }

        [UnityTest]
        public IEnumerator ПромоткаПереживаетПродолжениеГлавы()
        {
            yield return ПерваяГлаваПромотанаДоКонца();

            // ── ГРАНИЦА: следующая глава идёт встык, человек ничего не решал ─
            _stage.SetSaveContext(Новелла, "ch2", "/content/scripts/промотка-ch02.lvn");
            _stage.Play(Глава("вторая"));
            yield return null;

            Assert.IsTrue(_stage.Skipping,
                "промотка погасла на границе глав — перечитывающий обязан открыть меню и "
                + "включить её заново на каждой главе, хотя ни одного решения он не принимал");
        }

        /// <summary>ОБРАТНАЯ СТОРОНА, И ОНА ВАЖНЕЕ. Игрок ушёл в меню — кадр
        /// передан витрине. Главу, которую он выберет там сам, он собирается
        /// ЧИТАТЬ; промотка, воскресшая на ней, пролистала бы её у него на
        /// глазах.</summary>
        [UnityTest]
        public IEnumerator ПромоткаНеУходитВМенюВместеСИгроком()
        {
            yield return ПерваяГлаваПромотанаДоКонца();

            _stage.HandOver();   // именно так оболочка отдаёт кадр витрине
            _stage.SetSaveContext(Новелла, "ch7", "/content/scripts/промотка-ch07.lvn");
            _stage.Play(Глава("седьмая"));
            yield return null;

            Assert.IsFalse(_stage.Skipping,
                "промотка воскресла в главе, выбранной из витрины — игрок открыл её читать, "
                + "а она пролистывается сама");
        }

        /// <summary>ВТОРАЯ ОБРАТНАЯ СТОРОНА: игрок остановил промотку сам. Его
        /// решение обязано пережить границу так же, как переживает её
        /// невыключенная промотка.</summary>
        [UnityTest]
        public IEnumerator ОстановленнаяИгрокомПромоткаНеВоскресает()
        {
            _stage.SetSaveContext(Новелла, "ch1", "/content/scripts/промотка-ch01.lvn");
            _stage.Play(Глава("первая"));
            yield return null;
            _stage.StartSkip();
            _stage.StopSkip();   // тап по кадру или по значку режима
            yield return null;

            _stage.SetSaveContext(Новелла, "ch2", "/content/scripts/промотка-ch02.lvn");
            _stage.Play(Глава("вторая"));
            yield return null;

            Assert.IsFalse(_stage.Skipping,
                "промотка, выключенная игроком, включилась сама на следующей главе");
        }
    }
}
