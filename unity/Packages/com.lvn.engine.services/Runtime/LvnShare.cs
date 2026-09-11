using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// ПЕРЕДАЧА ПРОХОЖДЕНИЯ (TR-17) — отдать свой снимок другому человеку.
    ///
    /// <para>Идея Ильи: «поделиться сохранением, чтобы другой доиграл». У
    /// коммерческих новелл этого почти нет; ближайшее — книги-игры со
    /// состоянием в ссылке и AI Dungeon, где чужое приключение продолжают с той
    /// же точки.</para>
    ///
    /// <para>СНИМОК ОТДАЁТ САМ ИГРОК, сервер его не вычитывает: отдать СВОЁ
    /// может только тот, у кого оно на руках, и снимок получается ровно тем,
    /// что было на экране, а не тем, что успело доехать до облака.</para>
    /// </summary>
    public static class LvnShare
    {
        /// <summary>Что вернулось по чужому коду.</summary>
        public sealed class Taken
        {
            public string Code;
            public string Title;   // какая новелла
            public string Note;    // подпись автора ссылки
            public JObject Body;   // сам снимок
            public string Error;   // пусто — всё пришло
        }

        /// <summary>Отдать снимок. Возвращает код ссылки или пусто, если не
        /// вышло: без учётки сервер снимков не принимает — иначе он превратился
        /// бы в открытый файлохост.</summary>
        public static async Task<string> GiveAsync(string titleId, JObject snapshot, string note = null)
        {
            if (snapshot == null) return null;
            var req = new JObject
            {
                ["title"] = titleId ?? "",
                ["note"] = note ?? "",
                ["body"] = snapshot,
            };
            var (code, body) = await LvnBackend.PostAsync("/v1/share", req.ToString(Newtonsoft.Json.Formatting.None));
            var d = LvnBackend.Json(code, body);
            return d == null ? null : (string)d["code"];
        }

        /// <summary>Взять чужое прохождение по коду. Учётка получателю НЕ
        /// нужна: ссылку открывают из чата, и требовать вход до того, как
        /// человек увидел присланное, — верный способ его потерять.</summary>
        public static async Task<Taken> TakeAsync(string shareCode)
        {
            if (string.IsNullOrEmpty(shareCode)) return new Taken { Error = "no_code" };
            var (code, body) = await LvnBackend.GetAsync("/v1/share/" + shareCode.Trim().ToUpperInvariant());
            var d = LvnBackend.Json(code, body);
            if (d == null) return new Taken { Error = "offline" };
            var err = (string)d["error"];
            if (!string.IsNullOrEmpty(err)) return new Taken { Error = err };
            return new Taken
            {
                Code = (string)d["code"],
                Title = (string)d["title"],
                Note = (string)d["note"],
                Body = d["body"] as JObject,
            };
        }
    }
}
