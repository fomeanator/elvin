using System;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ОПАСНОЕ ДЕЛАЕТСЯ ВТОРЫМ НАЖАТИЕМ (<see cref="LvnAskTwice"/>).
    ///
    /// <para>Обряд был собран дважды — сброс устройства и удаление аккаунта, —
    /// и оба необратимы. Проверяется то, чего не видно глазами: первое нажатие
    /// НЕ делает, второе делает, подпись читает состояние (иначе смена языка
    /// вернула бы вид «Удалить» кнопке, оставшейся взведённой), а срок взвода
    /// не подчиняется ручке темпа движения.</para>
    ///
    /// <para>Отсчёт времени тут не проверяется: планировщик UITK живёт только
    /// на панели, а панели в этой среде нет. Проверяется то, что от времени не
    /// зависит, — и объявленное число секунд.</para>
    /// </summary>
    public class AskTwiceTests
    {
        [Test]
        public void ПервоеНажатиеНеДелаетВтороеДелает()
        {
            int сделано = 0;
            var b = new Button();
            LvnAskTwice.AskTwice(b, () => "Удалить", () => "Точно?", () => сделано++);

            using (var e = new NavigationSubmitEvent()) { }   // событий не шлём — жмём напрямую
            b.SendEvent(ClickEvent.GetPooled());
            Assert.AreEqual(0, сделано, "первое нажатие сделало необратимое без переспроса");
            b.SendEvent(ClickEvent.GetPooled());
            Assert.AreEqual(1, сделано, "второе нажатие обязано подтвердить");
        }

        /// <summary>
        /// ПОДПИСЬ ЧИТАЕТ СОСТОЯНИЕ, А НЕ НАЗНАЧАЕТСЯ ПО ШАГАМ.
        ///
        /// <para>Оба автора пришли к этому отдельно: со сменой языка привязка
        /// перечитывает источник, и назначенная руками надпись вернула бы вид
        /// «Удалить» кнопке, оставшейся взведённой, — а следующее нажатие
        /// снесло бы аккаунт без переспроса.</para>
        /// </summary>
        [Test]
        public void ПодписьСледуетЗаВзводом()
        {
            var b = new Button();
            LvnAskTwice.AskTwice(b, () => "Удалить", () => "Точно?", () => { });

            Assert.AreEqual("Удалить", b.text, "спокойная подпись");
            b.SendEvent(ClickEvent.GetPooled());
            Assert.AreEqual("Точно?", b.text, "взведённая кнопка обязана переспрашивать");
            LvnRedress.Refresh(b);
            Assert.AreEqual("Точно?", b.text,
                "перечитывание источника (смена языка) сбросило переспрос — "
                + "следующее нажатие сделало бы необратимое без него");
        }

        /// <summary>После подтверждения кнопка возвращается к спокойному
        /// виду: иначе второй заход начинается со взведённой.</summary>
        [Test]
        public void ПослеПодтвержденияКнопкаОстываает()
        {
            var b = new Button();
            LvnAskTwice.AskTwice(b, () => "Удалить", () => "Точно?", () => { });
            b.SendEvent(ClickEvent.GetPooled());
            b.SendEvent(ClickEvent.GetPooled());
            Assert.AreEqual("Удалить", b.text, "кнопка осталась взведённой после дела");
        }

        /// <summary>
        /// ОКНО ПЕРЕСПРОСА — НЕ АНИМАЦИЯ.
        ///
        /// <para>Обе копии брали срок через <c>LvnMotion.Ms</c>, потому что так
        /// берут длительность все движения оболочки. Игрок, попросивший меньше
        /// движения (темп 0.35), получал вместо четырёх секунд полторы;
        /// выкрученный темп оставлял кнопку взведённой на шестнадцать — ровно
        /// той миной, от которой обряд и заведён.</para>
        /// </summary>
        [Test]
        public void СрокВзводаНеЗависитОтТемпаДвижения()
        {
            float был = LvnMotion.Tempo;
            try
            {
                LvnMotion.Tempo = 0.35f;
                float медленный = LvnAskTwice.ArmedSeconds;
                LvnMotion.Tempo = 4f;
                Assert.AreEqual(медленный, LvnAskTwice.ArmedSeconds,
                    "время на решение поехало за ручкой темпа движения");
                Assert.AreEqual(4f, LvnAskTwice.ArmedSeconds, 0.001f);
            }
            finally { LvnMotion.Tempo = был; }
        }

        [Test]
        public void ПустойКнопкиХватаетМолча()
        {
            Assert.IsNull(LvnAskTwice.AskTwice(null, () => "a", () => "b", () => { }));
        }
    }
}
