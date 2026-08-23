using Lvn.UI.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ЛИПКОЕ СОСТОЯНИЕ sfx ОБЯЗАНО ПЕРЕЖИВАТЬ ЖИЗНЬ АКТЁРА.
    ///
    /// <para>Сценарий пишет «actor герой …» и следом «sfx id=герой dark=…» —
    /// одной пачкой. Актёр же строится асинхронно, а его слои пересобираются
    /// при догрузке арта. Из этого выросли две живые «вспышки» героя-голограммы:
    /// эффект, пришедший до рождения слота, терялся молча (герой выходил
    /// светлым), а пересборка слоёв уничтожала одетый материал (герой светлел
    /// посреди сцены, когда доезжал слой лица).</para>
    /// </summary>
    public class SpriteFxStickinessTests
    {
        private static Sprite NewSprite()
            => Sprite.Create(new Texture2D(2, 2), new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));

        private static Image NewLayer(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = NewSprite();
            return img;
        }

        [Test]
        public void Reskin_DressesLayersBornAfterTheEffect()
        {
            var actor = new GameObject("actor", typeof(RectTransform));
            try
            {
                var first = NewLayer(actor.transform, "layer:body");
                LvnSpriteFxDriver.Apply(actor, new JObject { ["dark"] = 0.9f });
                Assert.IsNotNull(first.material, "sanity: живой слой одет");
                var fxMat = first.material;
                Assert.AreNotEqual("Default UI Material", fxMat.name,
                    "sanity: это материал эффекта, а не дефолт uGUI");

                // Пересборка облика: старый слой умер, родились два новых —
                // ровно то, что делает Configure при догрузке слоя лица.
                Object.DestroyImmediate(first.gameObject);
                var body = NewLayer(actor.transform, "layer:body");
                var face = NewLayer(actor.transform, "layer:face");

                LvnSpriteFxDriver.Reskin(actor);

                Assert.AreEqual(fxMat.shader, body.material.shader,
                    "пересозданное тело рисуется дефолтным материалом — тёмный силуэт слетел");
                Assert.AreEqual(fxMat.shader, face.material.shader,
                    "новорождённый слой лица рисуется дефолтным материалом — светлая вспышка поверх силуэта");
            }
            finally { Object.DestroyImmediate(actor); }
        }

        [Test]
        public void PendingSfx_WaitsForTheActorToBeBorn()
        {
            var host = new GameObject("host");
            try
            {
                var stage = new WorldStage(host.transform, sortingOrder: 0);

                // Эффект приходит ДО того, как актёр построен, — раньше он молча
                // терялся и герой выходил без своего тёмного силуэта.
                Assert.IsTrue(stage.ApplySpriteFx("hero", new JObject { ["dark"] = 0.88f }),
                    "эффект по ещё не родившемуся актёру не смеет отвергаться");

                var actor = stage.EnsureActor("hero");
                Assert.IsNotNull(actor.GetComponent<LvnSpriteFxDriver>(),
                    "рождение актёра обязано доставить отложенный sfx");
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void PendingSfx_FullOffCancelsTheQueue()
        {
            var host = new GameObject("host");
            try
            {
                var stage = new WorldStage(host.transform, sortingOrder: 0);
                stage.ApplySpriteFx("hero", new JObject { ["dark"] = 0.88f });
                stage.ApplySpriteFx("hero", new JObject { ["off"] = 1 });

                var actor = stage.EnsureActor("hero");
                Assert.IsNull(actor.GetComponent<LvnSpriteFxDriver>(),
                    "снятый эффект не должен «догонять» актёра из очереди");
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void PendingSfx_LaterFieldsLayerOverEarlierOnes()
        {
            var host = new GameObject("host");
            try
            {
                var stage = new WorldStage(host.transform, sortingOrder: 0);
                stage.ApplySpriteFx("hero", new JObject { ["dark"] = 0.88f });
                stage.ApplySpriteFx("hero", new JObject { ["rim"] = 0.7f });

                var actor = stage.EnsureActor("hero");
                Assert.IsNotNull(actor.GetComponent<LvnSpriteFxDriver>(),
                    "слившиеся в очереди поля обязаны доехать одним применением");
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
