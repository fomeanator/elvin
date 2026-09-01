using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Services
{
    /// <summary>
    /// КОМНАТА НА ДВОИХ И БОЛЬШЕ — общий стол с именованными ящиками.
    ///
    /// <para>Здесь нет правил ни одной игры, и это главное. Почти любой
    /// мультиплеер — это ящик с ПРАВИЛОМ РАСКРЫТИЯ. Одновременный выбор (дуэль,
    /// камень-ножницы) — ящик, который не открывается, пока не положили все.
    /// Ход по очереди (шахматы, карты) — ящик, видный сразу. Гонка «кто первый»
    /// — тот же ящик плюс порядок, в котором в него клали. Разные игры — разная
    /// настройка одного механизма, а не разный код.</para>
    ///
    /// <para>Правила игры при этом считает СЦЕНАРИЙ, на каждом устройстве свой.
    /// Так можно, пока игра детерминирована: одинаковые входы дают одинаковый
    /// исход. Выигрыш не в экономии строк на сервере, а в том, что правила живут
    /// в одном месте — автор меняет их в <c>.lvns</c>, и сетевая игра меняется
    /// сама, без выкатки. Серверная копия правил разошлась бы с клиентской, и
    /// разошлась бы молча.</para>
    ///
    /// <para>Единственное, что обязан знать сервер, — ПОРЯДОК: «кто нажал
    /// раньше» из своей копии игры не вывести.</para>
    /// </summary>
    public static class LvnNetRoom
    {
        /// <summary>Код комнаты — его диктуют партнёру.</summary>
        public static string Code { get; private set; }
        /// <summary>Своё место: "a" у открывшего, "b" у следующего и так далее.</summary>
        public static string Seat { get; private set; }
        /// <summary>Сколько сейчас за столом.</summary>
        public static int Seats { get; private set; }
        /// <summary>Зерно случайности комнаты — одно на всех, кто в ней сидит.
        /// Ноль, пока не вошли.</summary>
        public static ulong Seed { get; private set; }
        /// <summary>Почему не вышло — пусто, если всё хорошо.</summary>
        public static string LastError { get; private set; }
        public static bool InRoom => !string.IsNullOrEmpty(Code) && !string.IsNullOrEmpty(_token);

        /// <summary>Порядок, в котором клали в последний прочитанный ящик.
        /// Для гонок: <c>Order[0]</c> — кто успел первым.</summary>
        public static IReadOnlyList<string> Order => _order;
        private static List<string> _order = new List<string>();

        // Токен места: право ходить за этого игрока. Не путать с токеном
        // аккаунта — комната про аккаунты не знает, и партнёру не нужно ни
        // регистрироваться, ни входить.
        private static string _token;

        // Сколько всего ждём, прежде чем сдаться. Партнёр может отойти за чаем;
        // полчаса — это «он не вернётся», а не «он думает».
        private const int WaitTotalSeconds = 1800;
        // Длина одного ожидания. Сервер держит соединение не дольше тридцати
        // секунд, и не он один: операторы и прокси рвут висящие соединения
        // примерно на том же рубеже.
        private const int WaitChunkSeconds = 25;

        /// <summary>Открыть комнату. Код ложится в <see cref="Code"/>.</summary>
        public static async Task<bool> OpenAsync()
        {
            LastError = null;
            var (code, body) = await SendAsync("POST", "/v1/net/rooms", "{}", null);
            if (!LvnBackend.Ok(code)) return Fail("комната не открылась", code, body);
            return Seated(body);
        }

        /// <summary>Сесть за стол партнёра по коду.</summary>
        public static async Task<bool> JoinAsync(string roomCode)
        {
            LastError = null;
            roomCode = (roomCode ?? "").Trim().ToUpperInvariant();
            if (roomCode.Length < 3) return Fail("код слишком короткий", 0, roomCode);

            var (code, body) = await SendAsync("POST", $"/v1/net/rooms/{roomCode}/join", "{}", null);
            if (code == 404) return Fail("комната не найдена", code, body);
            if (code == 409) return Fail("за столом нет мест", code, body);
            if (!LvnBackend.Ok(code)) return Fail("не удалось сесть", code, body);
            return Seated(body);
        }

        /// <summary>Дождаться, пока за столом наберётся <paramref name="need"/>
        /// человек. До этого партию начинать нельзя.</summary>
        public static async Task<bool> WaitSeatsAsync(int need)
        {
            LastError = null;
            if (!InRoom) return Fail("не в комнате", 0, null);
            var until = DateTime.UtcNow.AddSeconds(WaitTotalSeconds);
            while (DateTime.UtcNow < until)
            {
                var (code, body) = await SendAsync(
                    "GET", $"/v1/net/rooms/{Code}?need={need}&wait={WaitChunkSeconds}", null, _token);
                if (!LvnBackend.Ok(code)) return Fail("связь потеряна", code, body);
                try
                {
                    var o = JObject.Parse(body);
                    Seats = (int?)o["seats"] ?? 0;
                    if (Seats >= need) return true;
                }
                catch { /* повторим */ }
            }
            return Fail("никто не пришёл", 0, null);
        }

        /// <summary>
        /// Положить своё в ящик. <paramref name="reveal"/>: "all" — откроется,
        /// когда положат все; "now" — видно сразу.
        /// </summary>
        public static async Task<bool> PutAsync(string key, string value, string reveal)
        {
            LastError = null;
            if (!InRoom) return Fail("не в комнате", 0, null);
            var payload = new JObject { ["value"] = value ?? "", ["reveal"] = reveal ?? "all" }.ToString();
            var (code, body) = await SendAsync("POST", CellPath(key), payload, _token);
            // 409 — наше же значение уже лежит (переподключение, повтор). Это не
            // ошибка: дальше просто читаем ящик.
            if (!LvnBackend.Ok(code) && code != 409) return Fail("не отправилось", code, body);
            return true;
        }

        /// <summary>
        /// Заглянуть в ящик. Ждёт, пока он откроется, если <paramref name="wait"/>.
        /// Возвращает чужие значения по местам или null, если не дождались.
        /// </summary>
        public static async Task<Dictionary<string, string>> GetAsync(string key, bool wait)
        {
            LastError = null;
            if (!InRoom) { Fail("не в комнате", 0, null); return null; }

            var until = DateTime.UtcNow.AddSeconds(wait ? WaitTotalSeconds : 0);
            do
            {
                int chunk = wait ? WaitChunkSeconds : 0;
                var (code, body) = await SendAsync("GET", CellPath(key) + "?wait=" + chunk, null, _token);
                if (!LvnBackend.Ok(code)) { Fail("связь потеряна", code, body); return null; }
                JObject o;
                try { o = JObject.Parse(body); } catch { continue; }

                _order = new List<string>();
                if (o["order"] is JArray ord)
                    foreach (var t in ord) _order.Add(t?.ToString() ?? "");

                if ((bool?)o["open"] == true)
                {
                    var others = new Dictionary<string, string>();
                    if (o["others"] is JObject map)
                        foreach (var kv in map) others[kv.Key] = kv.Value?.ToString() ?? "";
                    return others;
                }
                if (!wait) return null;   // закрыт, и ждать не просили
            } while (DateTime.UtcNow < until);

            Fail("никто не ответил", 0, null);
            return null;
        }

        /// <summary>Забыть комнату. Сервер уберёт её сам, когда в неё перестанут
        /// ходить.</summary>
        public static void Leave()
        {
            Code = null; Seat = null; _token = null; Seats = 0; Seed = 0UL;
            _order = new List<string>();
        }

        // ── низ ─────────────────────────────────────────────────────────────

        // Ключ ящика придумывает автор и может написать что угодно, включая
        // пробелы и кириллицу, — экранируем.
        private static string CellPath(string key) =>
            $"/v1/net/rooms/{Code}/cells/{UnityWebRequest.EscapeURL(key ?? "")}";

        private static bool Seated(string body)
        {
            try
            {
                var o = JObject.Parse(body);
                Code = (string)o["code"];
                Seat = (string)o["seat"];
                Seats = (int?)o["seats"] ?? 1;
                Seed = (ulong?)o["seed"] ?? 0UL;
                _token = (string)o["token"];
                return InRoom;
            }
            catch { return Fail("непонятный ответ", 0, body); }
        }

        private static bool Fail(string what, long code, string body)
        {
            LastError = what;
            Debug.LogWarning($"[lvn-net] {what} (код {code}) {Trim(body)}");
            return false;
        }

        // Через дом обрезки: здесь предел рвал суррогатную пару пополам, и
        // по проводу уходила строка, которую принимающая сторона не разберёт.
        private static string Trim(string s) => Lvn.LvnClip.Head(s, 200);

        /// <summary>
        /// Свой запрос, а не общий <see cref="LvnBackend"/>: комната
        /// подписывается токеном МЕСТА, и он не имеет отношения к аккаунту.
        /// Токен идёт заголовком, а не в адресе — адреса оседают в логах прокси.
        /// </summary>
        private static async Task<(long code, string body)> SendAsync(
            string method, string path, string json, string token)
        {
            var url = Lvn.LvnUrl.Base(LvnBackend.BaseUrl) + path;
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
