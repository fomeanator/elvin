using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.Services
{
    /// <summary>
    /// Отзыв прямо из игры.
    ///
    /// <para>Тестер, который пишет «тут баг» в мессенджер, не назовёт ни сборку,
    /// ни главу, ни место в сценарии — он их не знает и знать не должен. Через
    /// неделю такое сообщение нельзя ни воспроизвести, ни отнести к версии.</para>
    ///
    /// <para>Поэтому текст — половина записи. Вторая половина собирается сама:
    /// номер сборки, новелла, глава, индекс команды, устройство и хвост лога.
    /// Кадр (реплику и фон) достраивает сервер — глава у него есть, а лишние
    /// килобайты с телефона стоят батареи.</para>
    /// </summary>
    public static class LvnFeedback
    {
        /// <summary>Хвост лога для отзыва. Ставится оболочкой (LvnLogShip
        /// держит кольцевой буфер): именно эти строки превращают «всё
        /// сломалось» в чинибельное сообщение.</summary>
        public static System.Func<string> TailLog;

        /// <summary>Где игрок находится прямо сейчас. Ставит оболочка —
        /// движок про отзывы не знает и знать не должен.</summary>
        public static string CurrentTitle, CurrentChapter, CurrentLabel;
        public static int CurrentAt;

        /// <summary>
        /// Отправляет отзыв. Возвращает false, если не дошло — вызывающему
        /// экрану надо сказать человеку правду, а не показать «спасибо» и
        /// потерять текст.
        /// </summary>
        public static async Task<bool> SendAsync(string text, string kind = "bug")
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (string.IsNullOrEmpty(LvnBackend.BaseUrl)) return false;

            var body = new JObject
            {
                ["text"] = text.Trim(),
                ["kind"] = kind,
                ["build"] = Application.version,
                ["device"] = SystemInfo.deviceModel,
            };
            if (!string.IsNullOrEmpty(CurrentTitle)) body["title"] = CurrentTitle;
            if (!string.IsNullOrEmpty(CurrentChapter)) body["chapter"] = CurrentChapter;
            if (!string.IsNullOrEmpty(CurrentLabel)) body["label"] = CurrentLabel;
            if (CurrentAt > 0) body["at"] = CurrentAt;
            var tail = TailLog?.Invoke();
            if (!string.IsNullOrEmpty(tail)) body["log"] = tail;

            var (code, _) = await LvnBackend.PostAsync("/v1/feedback", body.ToString());
            return code == 200;
        }
    }
}
