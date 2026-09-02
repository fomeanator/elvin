using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ОПАСНОЕ ДЕЛАЕТСЯ ВТОРЫМ НАЖАТИЕМ — кнопка взводится, переспрашивает и
    /// сама остывает.
    ///
    /// <para>Обряд собирали дважды и по-разному: сброс устройства в настройках
    /// и удаление аккаунта в профиле. Оба необратимы, и оба автора отдельно
    /// пришли к одному неочевидному правилу — надпись обязана ЧИТАТЬ состояние
    /// через привязку, а не назначаться по шагам: со сменой языка привязка
    /// перечитывает источник, и назначенная руками надпись вернула бы вид
    /// «Удалить» у кнопки, оставшейся взведённой. Следующее нажатие снесло бы
    /// аккаунт без переспроса.</para>
    ///
    /// <para><b>ОКНО ПЕРЕСПРОСА — НЕ АНИМАЦИЯ.</b> Обе копии брали срок через
    /// <see cref="LvnMotion.Ms"/>, потому что так берут длительность все
    /// движения оболочки, — и попадали под ручку темпа. Игрок, попросивший
    /// МЕНЬШЕ ДВИЖЕНИЯ, получал вместо четырёх секунд на «передумать» —
    /// полторы; выкрученный темп, наоборот, оставлял кнопку взведённой на
    /// шестнадцать секунд, то есть ровно той миной, от которой обряд и заведён.
    /// Здесь срок живёт своим числом и ручке темпа не подчиняется: это не
    /// размах движения, а время на решение.</para>
    ///
    /// <para><b>СНЯТИЕ С ЭКРАНА РАЗОРУЖАЕТ.</b> Обе копии полагались на таймер,
    /// а таймер элемента живёт, пока элемент на панели: закрыл экран взведённым
    /// — таймер встал, и кнопка дождалась игрока взведённой. Мину эту обе
    /// копии называли в комментарии и ни одна не закрывала.</para>
    ///
    /// <para>Вид остаётся вызывающему: подкраска на время взвода — довод, а не
    /// обязанность. Механизм отвечает за КАК (взвод, срок, разоружение,
    /// подпись), а как выглядит опасность на конкретном экране — решает
    /// экран.</para>
    /// </summary>
    public static class LvnAskTwice
    {
        /// <summary>Сколько секунд кнопка остаётся взведённой. Одно число на
        /// оба экрана: у настроек оно было «три уведомления», у профиля —
        /// своя константа экрана, и совпадать они не обязаны были ничем.</summary>
        public const float ArmedSeconds = 4f;

        /// <summary>
        /// ВЗВЕСТИ КНОПКУ. <paramref name="calm"/> и <paramref name="armed"/> —
        /// источники подписи (не строки: подпись переживает смену языка),
        /// <paramref name="confirmed"/> зовётся вторым нажатием.
        /// </summary>
        /// <param name="armedTint">Чем красить взведённую. <c>null</c> — не
        /// красить: у кнопки-значения в ряду настроек своя тихая манера, и
        /// заливка сломала бы ряд.</param>
        public static Button AskTwice(Button b, Func<string> calm, Func<string> armed,
                                      Action confirmed, Color? armedTint = null)
        {
            if (b == null || calm == null || armed == null) return b;
            var latch = new LvnArming();
            Color calmBg = b.style.backgroundColor.value;
            Color calmFg = b.style.color.value;

            LvnRedress.Bind(b, () => latch.Armed ? armed() : calm());

            IVisualElementScheduledItem cooling = null;
            void Paint()
            {
                LvnRedress.Refresh(b);
                if (!armedTint.HasValue) return;
                b.style.backgroundColor = latch.Armed ? armedTint.Value : calmBg;
                b.style.color = latch.Armed ? Color.white : calmFg;
            }
            void Disarm()
            {
                cooling?.Pause();
                if (latch.Disarm()) Paint();
            }

            // Снятие с экрана разоружает — см. заголовок. Таймер здесь не
            // помощник: он и сам останавливается вместе с элементом.
            b.RegisterCallback<DetachFromPanelEvent>(_ => Disarm());

            b.clicked += () =>
            {
                if (!latch.Press())
                {
                    Paint();
                    // Прежний срок гасим: иначе взвод, сделанный после
                    // остывания, разоружит старый таймер прошлого взвода.
                    cooling?.Pause();
                    cooling = b.schedule.Execute(Disarm);
                    cooling.ExecuteLater((long)(ArmedSeconds * 1000f));
                    return;
                }
                cooling?.Pause();
                Paint();          // взвод снят самим Press — рисуем спокойный вид
                confirmed?.Invoke();
            };
            return b;
        }
    }
}
