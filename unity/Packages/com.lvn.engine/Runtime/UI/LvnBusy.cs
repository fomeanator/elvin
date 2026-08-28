using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// КНОПКА, КОТОРАЯ ЖДЁТ, ОТПУСКАЕТ СЕБЯ САМА — чем бы ожидание ни кончилось.
    ///
    /// <para>Обряд «нажали → выключить → дождаться → включить» стоял в девяти
    /// местах, и ни одно не было записано целиком одинаково. Двое обернули его в
    /// <c>try/finally</c> (покупка в гардеробе и в лавке паков — там за замок
    /// уже платили дефектом), остальные положились на то, что ожидание кончится
    /// успехом.</para>
    ///
    /// <para>Ожидание успехом не кончается. Вход через провайдера, кнопка
    /// «Играть» в карточке новеллы и «Стереть загруженное» в настройках ждали
    /// сеть без страховки: одно исключение — и кнопка выключена НАВСЕГДА, а
    /// подпись осталась «Connecting…». Игра при этом цела, лог чист, и починка у
    /// игрока одна — перезапуск. Хуже всего это на кнопке «Играть»: она стоит на
    /// главном пути, и «игра не запускается» игрок объяснит себе сам, не в нашу
    /// пользу.</para>
    ///
    /// <para>Здесь же живёт защита от второго нажатия: пока идёт работа, кнопка
    /// выключена, а <see cref="RunAsync"/> ещё и молча отклоняет повторный вход
    /// — тапы успевают пройти до того, как выключение доедет до отрисовки.</para>
    ///
    /// <para><paramref name="releaseOnSuccess"/> нужен там, где работа САМА
    /// расставляет состояние кнопки в конце (настройки перестраивают строку
    /// после очистки диска). При провале кнопка отпускается всегда — это и есть
    /// смысл дома.</para>
    /// </summary>
    public static class LvnBusy
    {
        /// <summary>Подписать нажатие, которое ждёт. Кнопка сама станет занятой
        /// и сама освободится.</summary>
        public static void OnClick(Button b, Func<Task> work, string busyText = "…",
                                   bool releaseOnSuccess = true, string what = null)
        {
            if (b == null || work == null) return;
            b.clicked += () => Lvn.LvnAsync.Fire(
                RunAsync(b, work, busyText, releaseOnSuccess, what), what ?? "BusyClick");
        }

        /// <summary>Провести работу с занятой кнопкой. Возвращает <c>false</c>,
        /// если кнопка уже занята или работа сорвалась.</summary>
        public static async Task<bool> RunAsync(Button b, Func<Task> work, string busyText = "…",
                                                bool releaseOnSuccess = true, string what = null)
        {
            if (work == null) return false;
            if (b == null) { await work(); return true; }
            if (!b.enabledSelf) return false;   // уже занята — второй тап не в счёт

            string label = b.text;
            b.SetEnabled(false);
            if (busyText != null) b.text = busyText;
            try
            {
                await work();
                if (releaseOnSuccess) Release(b, label, busyText);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[lvn-busy] {what ?? label}: {e.Message}");
                Release(b, label, busyText);   // провал — отпускаем ВСЕГДА
                return false;
            }
        }

        // Подпись возвращается только если её не поменяла сама работа: «Готово»
        // после покупки не должно превращаться обратно в «Купить».
        private static void Release(Button b, string label, string busyText)
        {
            b.SetEnabled(true);
            if (busyText != null && b.text == busyText) b.text = label;
        }
    }
}
