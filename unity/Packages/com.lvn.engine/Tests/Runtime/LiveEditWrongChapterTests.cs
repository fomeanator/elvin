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
    /// ЖИВАЯ ПРАВКА ЛОЖИТСЯ ТОЛЬКО В СВОЮ ГЛАВУ.
    ///
    /// <para>Автор правит главу, пока её читают. Правка едет по сети — и пока
    /// она едет, игрок успевает дочитать главу и уйти в следующую. Пришедший
    /// текст относится к главе, которой на экране уже нет.</para>
    ///
    /// <para>Хост проверял главу ДО похода в сеть и не проверял после: между
    /// проверкой и применением стоял `await`, а в нём умещается смена главы.
    /// Опознание поэтому живёт и в сцене: хостов у движка несколько (оболочка,
    /// экспортированный проект, чужая встройка), а цена ошибки одна — игрок
    /// читает четвёртую главу и вдруг оказывается в третьей.</para>
    /// </summary>
    public class LiveEditWrongChapterTests
    {
        private const string Новелла = "стенд-живая-правка";
        private const string АдресТретьей = "/content/scripts/правка-ch03.lvn";
        private const string АдресЧетвёртой = "/content/scripts/правка-ch04.lvn";

        private static string Глава(string реплика) => @"{""scene"":""сцена"",""script"":[
            {""op"":""say"",""text"":""" + реплика + @"""},
            {""op"":""say"",""text"":""вторая строка""}]}";

        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            _stage = TestStage.Panel("live-edit-wrong-chapter", out _go, out _panel);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Уборка()
        {
            _stage?.ClearStage();
            _stage = null;
            yield return null;
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            yield return null;
        }

        private bool ВИстории(string текст)
        {
            foreach (var строка in _stage.Backlog)
                if (строка.text == текст) return true;
            return false;
        }

        [UnityTest]
        public IEnumerator ПравкаДогналаУшедшуюГлаву_НаЭкранЕёНеПускают()
        {
            // Игрок читал третью главу…
            _stage.SetSaveContext(Новелла, "ch3", АдресТретьей);
            _stage.Play(Глава("в третьей главе"));
            yield return null;

            // …дочитал и ушёл в четвёртую, пока правка третьей ехала по сети.
            _stage.SetSaveContext(Новелла, "ch4", АдресЧетвёртой);
            _stage.Play(Глава("в четвёртой главе"));
            yield return null;

            var исход = _stage.ApplyLiveEdit(АдресТретьей, Глава("правленая третья"));

            Assert.AreEqual(VnStage.LiveEdit.Stale, исход,
                "сцена приняла правку главы, которой на ней нет");
            Assert.IsFalse(ВИстории("правленая третья"),
                "текст ушедшей главы встал на экран поверх той, которую игрок читает — "
                + "человек читал четвёртую и вдруг оказался в третьей");
            Assert.IsTrue(ВИстории("в четвёртой главе"),
                "стенд: четвёртая глава не игралась — мерить было нечего");
        }

        /// <summary>ОБРАТНАЯ СТОРОНА: правка СВОЕЙ главы обязана применяться,
        /// иначе опознание превратилось бы в глухую стену и живое обновление
        /// перестало бы работать вовсе.</summary>
        [UnityTest]
        public IEnumerator ПравкаСвоейГлавы_ЛожитсяНаМесто()
        {
            _stage.SetSaveContext(Новелла, "ch3", АдресТретьей);
            _stage.Play(Глава("в третьей главе"));
            yield return null;

            var исход = _stage.ApplyLiveEdit(АдресТретьей, Глава("правленая третья"));

            Assert.AreNotEqual(VnStage.LiveEdit.Stale, исход,
                "сцена не приняла правку СВОЕЙ главы — живое обновление перестало работать");
            yield return null;
            Assert.IsTrue(ВИстории("правленая третья") || исход == VnStage.LiveEdit.Swapped,
                "правка принята, но новый текст на экран не попал");
        }

        /// <summary>Правка без адреса — тоже чужая: у хоста, не сказавшего, чья
        /// это глава, нет способа доказать, что она своя.</summary>
        [UnityTest]
        public IEnumerator ПравкаБезАдреса_НеПринимается()
        {
            _stage.SetSaveContext(Новелла, "ch3", АдресТретьей);
            _stage.Play(Глава("в третьей главе"));
            yield return null;

            Assert.AreEqual(VnStage.LiveEdit.Stale, _stage.ApplyLiveEdit(null, Глава("ничья")),
                "правка без адреса главы принята");
            Assert.AreEqual(VnStage.LiveEdit.Stale, _stage.ApplyLiveEdit("", Глава("ничья")),
                "правка с пустым адресом главы принята");
        }
    }
}
