using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Services
{
    /// <summary>
    /// ДУЭЛЬ ВДВОЁМ ПО СЕТИ — обмен планами, а не игра на сервере.
    ///
    /// <para>Правила боя целиком остаются в скрипте. Сервер знает только
    /// комнату, две скамьи и почтовый ящик на обмен: кто что выбрал. Так можно
    /// потому, что дуэль детерминирована — в ней ноль случайности, — и оба
    /// клиента, получив оба плана, посчитают ОДИН И ТОТ ЖЕ исход тем же кодом,
    /// что играет одиночную партию.</para>
    ///
    /// <para>Выгода не в экономии строк на сервере, а в том, что правила живут
    /// в одном месте. Автор меняет матрицу ходов в <c>.lvns</c> — и сетевой бой
    /// меняется сам, без выкатки сервера. Серверная копия правил разошлась бы с
    /// клиентской, и разошлась бы молча.</para>
    ///
    /// <para>Ожидание чужого хода — ДОЛГОЕ: запрос висит, пока партнёр думает,
    /// и возвращается в тот же миг, когда он нажал. Опрос по таймеру дал бы
    /// задержку в полсекунды-секунду на каждом обмене, и бой ощущался бы вялым
    /// независимо от скорости сети.</para>
    /// </summary>
    public static class LvnDuelOnline
    {
        /// <summary>Код комнаты — его диктуют партнёру.</summary>
        public static string Code { get; private set; }
        /// <summary>Своя скамья: "a" (создал) или "b" (вошёл).</summary>
        public static string Seat { get; private set; }
        /// <summary>Почему не вышло — пусто, если всё хорошо.</summary>
        public static string LastError { get; private set; }
        public static bool InRoom => !string.IsNullOrEmpty(Code) && !string.IsNullOrEmpty(_token);

        // Токен скамьи: право ходить за этого игрока. Не путать с токеном
        // аккаунта — комната ни про какие аккаунты не знает, и партнёру не
        // нужно ни регистрироваться, ни входить.
        private static string _token;

        // Сколько всего ждём чужой ход, прежде чем сдаться. Партнёр может
        // отойти за чаем; полчаса — это «он не вернётся», а не «он думает».
        private const int WaitTotalSeconds = 1800;
        // Длина одного ожидания. Сервер держит соединение не дольше тридцати
        // секунд, и не он один: мобильные операторы и прокси рвут висящие
        // соединения примерно на этом же рубеже.
        private const int WaitChunkSeconds = 25;

        /// <summary>Создать комнату. Код кладётся в <see cref="Code"/>.</summary>
        public static async Task<bool> HostAsync()
        {
            LastError = null;
            var (code, body) = await SendAsync("POST", "/v1/duel/rooms", "{}", null);
            if (code != 200) return Fail("комната не создалась", code, body);
            return Seated(body);
        }

        /// <summary>Войти в комнату партнёра по коду.</summary>
        public static async Task<bool> JoinAsync(string roomCode)
        {
            LastError = null;
            roomCode = (roomCode ?? "").Trim().ToUpperInvariant();
            if (roomCode.Length < 3) return Fail("код слишком короткий", 0, roomCode);

            var (code, body) = await SendAsync("POST", $"/v1/duel/rooms/{roomCode}/join", "{}", null);
            if (code == 404) return Fail("комната не найдена", code, body);
            if (code == 409) return Fail("в комнате уже двое", code, body);
            if (code != 200) return Fail("не удалось войти", code, body);
            return Seated(body);
        }

        /// <summary>
        /// Один обмен: сдать свой план и дождаться чужого.
        ///
        /// <para>Возвращает план соперника или null, если не дождались. Свой
        /// план сдаётся ДО ожидания и ровно один раз — сервер отвергнет попытку
        /// переиграть его, увидев чужой.</para>
        /// </summary>
        public static async Task<string> ExchangeAsync(int round, string plan)
        {
            LastError = null;
            if (!InRoom) { Fail("не в комнате", 0, null); return null; }

            var payload = new JObject { ["round"] = round, ["actions"] = plan ?? "" }.ToString();
            var (code, body) = await SendAsync("POST", $"/v1/duel/rooms/{Code}/plan", payload, _token);
            // 409 — наш же план уже лежит (переподключение, повтор). Это не
            // ошибка: дальше просто ждём чужой.
            if (code != 200 && code != 409) { Fail("ход не ушёл", code, body); return null; }

            var until = DateTime.UtcNow.AddSeconds(WaitTotalSeconds);
            while (DateTime.UtcNow < until)
            {
                var (sc, sb) = await SendAsync(
                    "GET", $"/v1/duel/rooms/{Code}?round={round}&wait={WaitChunkSeconds}", null, _token);
                if (sc != 200) { Fail("связь потеряна", sc, sb); return null; }
                JObject st;
                try { st = JObject.Parse(sb); } catch { continue; }
                var theirs = (string)st["opponent_actions"];
                if (!string.IsNullOrEmpty(theirs)) return theirs;
                // Не дождались за этот отрезок — заходим снова. Отдельной паузы
                // не нужно: ожидание само по себе и есть пауза.
            }
            Fail("соперник не ответил", 0, null);
            return null;
        }

        /// <summary>Забыть комнату (вышли из боя).</summary>
        public static void Leave()
        {
            Code = null; Seat = null; _token = null;
        }

        // ── низ ─────────────────────────────────────────────────────────────

        private static bool Seated(string body)
        {
            try
            {
                var o = JObject.Parse(body);
                Code = (string)o["code"];
                Seat = (string)o["seat"];
                _token = (string)o["token"];
                return InRoom;
            }
            catch { return Fail("непонятный ответ", 0, body); }
        }

        private static bool Fail(string what, long code, string body)
        {
            LastError = what;
            Debug.LogWarning($"[lvn-duel] {what} (код {code}) {Trim(body)}");
            return false;
        }

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);

        /// <summary>
        /// Свой запрос, а не общий <see cref="LvnBackend"/>: комната
        /// подписывается токеном СКАМЬИ, и он не имеет отношения к аккаунту.
        /// Ставим его заголовком, а не в адрес — адреса оседают в логах прокси,
        /// а этот токен есть право ходить за игрока.
        /// </summary>
        private static async Task<(long code, string body)> SendAsync(
            string method, string path, string json, string token)
        {
            var url = (LvnBackend.BaseUrl ?? "").TrimEnd('/') + path;
            using (var req = new UnityWebRequest(url, method))
            {
                if (json != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", "Bearer " + token);
                // Таймаут не ставим: долгое ожидание висит по замыслу, и
                // клиентский таймаут обрубал бы его ровно тогда, когда оно
                // работает.
                try
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone) await Task.Yield();
                    return (req.responseCode, req.downloadHandler?.text ?? "");
                }
                catch (Exception e) { return (0, e.Message); }
            }
        }
    }
}
