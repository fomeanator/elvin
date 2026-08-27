using System.Collections;
using Newtonsoft.Json.Linq;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// СТВОР ПОРТАЛА: три отказа, которые ловятся без глаз.
    ///
    /// <para>Портал «то показывался, то нет» неделю чинился догадками, потому
    /// что проверить его можно было только глазами на живом устройстве. Эти
    /// проверки закрывают ровно те отказы, что случались: команда не дошла до
    /// сцены, слой не рисуется, слой не за героиней.</para>
    ///
    /// <para>Раньше створ был полноэкранным эффектом и жил на КАМЕРЕ — в сцене
    /// без камеры он молча ничего не делал, а уборка сцены сбрасывала его
    /// посреди перехода. Тест поднимает сцену без единой камеры: если створ
    /// снова станет постэффектом, первая же проверка это поймает.</para>
    /// </summary>
    public class PortalLayerTests
    {
        private GameObject _go;
        private VnStage _stage;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("portal-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _stage = _go.AddComponent<VnStage>();
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        private static GameObject Portal() => GameObject.Find("vn-portal");

        [UnityTest]
        public IEnumerator OpenCommandReachesTheStage()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""кадр""}]}");
            yield return null;

            var portal = Portal();
            Assert.IsNotNull(portal, "слоя створа нет в сцене — команде некуда приходить");

            _stage.ApplyStage(new JObject
            {
                ["op"] = "portal", ["open"] = 1f, ["x"] = 0.72f, ["y"] = 0.52f,
                ["radius"] = 0.30f, ["dur"] = 0f,
            });
            yield return null;

            var image = portal.GetComponent<UnityEngine.UI.RawImage>();
            Assert.IsTrue(image != null && image.enabled,
                "створ раскрыт, но слой не рисуется — ровно так выглядел «портал через раз»");
        }

        [UnityTest]
        public IEnumerator ClosedPortalDrawsNothing()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""кадр""}]}");
            yield return null;
            _stage.ApplyStage(new JObject { ["op"] = "portal", ["open"] = 1f, ["dur"] = 0f });
            yield return null;
            _stage.ApplyStage(new JObject { ["op"] = "portal", ["open"] = 0f, ["dur"] = 0f });
            yield return null;
            yield return null;

            var image = Portal()?.GetComponent<UnityEngine.UI.RawImage>();
            Assert.IsTrue(image == null || !image.enabled,
                "закрытый створ обязан гаснуть целиком, а не висеть прозрачным слоем поверх сцены");
        }

        // «Портал должен быть под героиней» — это не настройка, а место в
        // иерархии: между фоном и актёрами. Постэффектом такое невозможно в
        // принципе, поэтому проверка заодно сторожит саму архитектуру.
        [UnityTest]
        public IEnumerator PortalStandsBehindActors()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""кадр""}]}");
            yield return null;

            var portal = Portal();
            Assert.IsNotNull(portal);
            var content = GameObject.Find("content");
            Assert.IsNotNull(content, "sanity: слой актёров называется content");

            Assert.AreSame(portal.transform.parent, content.transform.parent,
                "створ и актёры обязаны жить в одном корне, иначе порядок между ними не определён");
            Assert.Less(portal.transform.GetSiblingIndex(), content.transform.GetSiblingIndex(),
                "створ рисуется ПОВЕРХ актёров — героиня окажется внутри портала, а не перед ним");
        }

        // Уборка сцены сбрасывала стек эффектов и гасила створ посреди
        // перехода: «портал при входе не показывается». Слой обязан пережить.
        [UnityTest]
        public IEnumerator PortalSurvivesASceneWipe()
        {
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""первая""}]}");
            yield return null;
            _stage.ApplyStage(new JObject { ["op"] = "portal", ["open"] = 1f, ["dur"] = 0f });
            yield return null;

            _stage.ClearStage();   // уборка сцены — та самая, что гасила эффект
            yield return null;

            var image = Portal()?.GetComponent<UnityEngine.UI.RawImage>();
            Assert.IsTrue(image != null && image.enabled,
                "уборка сцены погасила створ — глава снова откроется без портала");
        }
    }
}
