using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Services
{
    /// <summary>
    /// The device-account session against the LVN product services (auth /
    /// wallet / analytics). Anonymous, mobile-style: a random device secret is
    /// minted once and kept in PlayerPrefs; <see cref="EnsureRegisteredAsync"/>
    /// exchanges it for a bearer token (idempotent — the same device always
    /// gets the same account back, e.g. after a reinstall-with-backup).
    /// Everything is optional: a game that never calls this plays fully
    /// offline, exactly as before.
    /// </summary>
    public static class LvnBackend
    {
        private const string PDevice = "lvn.svc.device";
        private const string PToken = "lvn.svc.token";
        private const string PUser = "lvn.svc.user";
        private const string PName = "lvn.svc.name";

        /// <summary>Server base url, e.g. "http://127.0.0.1:8077". The host sets
        /// it once at boot (NovelApp's ServerUrl is the usual source).</summary>
        public static string BaseUrl = "";

        public static string UserId => LvnKeep.Get(PUser, "");
        public static string Token => LvnKeep.Get(PToken, "");
        public static bool SignedIn => !string.IsNullOrEmpty(Token);

        /// <summary>Raised after a successful (re-)registration.</summary>
        public static event Action<string> SignedInChanged;

        /// <summary>Register (or recover) the device account. Safe to call every
        /// boot; no-ops offline and keeps the previous token.</summary>
        public static async Task<bool> EnsureRegisteredAsync()
        {
            if (string.IsNullOrEmpty(BaseUrl)) return SignedIn;
            // Метка устройства — у ПАСПОРТИСТА: её потеря регистрирует НОВУЮ
            // учётку, то есть отнимает кошелёк и покупки, поэтому дома два.
            var device = LvnMark.Steady(PDevice);
            var body = JsonUtility.ToJson(new RegisterReq { device_id = device });
            var (code, json) = await PostAsync("/v1/auth/register", body, auth: false);
            if (!Ok(code) || string.IsNullOrEmpty(json)) return SignedIn;
            var resp = JsonUtility.FromJson<RegisterResp>(json);
            if (string.IsNullOrEmpty(resp?.token)) return SignedIn;
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(PToken, resp.token);
                LvnKeep.Put(PUser, resp.user_id);
            }
            LvnWallet.NoteUser(resp.user_id); // bind (or reset) the offline wallet to this account
            SignedInChanged?.Invoke(resp.user_id);
            return true;
        }

        [Serializable] private class RegisterReq { public string device_id; }
        [Serializable] private class RegisterResp { public string user_id; public string token; }

        /// <summary>The profile display name — local-first (kept in PlayerPrefs
        /// even offline), synced to the account when a server is reachable.</summary>
        public static string DisplayName => LvnKeep.Get(PName, "");

        /// <summary>Save the display name locally and push it to the account
        /// (POST /v1/auth/profile). Offline the local copy still sticks — the
        /// next successful call syncs it.</summary>
        public static async Task<bool> SetDisplayNameAsync(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            LvnKeep.Put(PName, name);
            var (code, _) = await PostAsync("/v1/auth/profile", JsonUtility.ToJson(new ProfileReq { name = name }));
            return Ok(code);
        }

        [Serializable] private class ProfileReq { public string name; }

        /// <summary>Sign in with a verified platform identity (POST
        /// /v1/auth/login) — cross-device recovery: a known identity returns
        /// its account and this device switches to it (token + user id are
        /// replaced); an unknown identity gets a fresh account.</summary>
        public static async Task<bool> LoginWithProviderAsync(string provider, string token)
        {
            var body = JsonUtility.ToJson(new ProviderReq { provider = provider, token = token });
            var (code, json) = await PostAsync("/v1/auth/login", body, auth: false);
            if (!Ok(code) || string.IsNullOrEmpty(json)) return false;
            var resp = JsonUtility.FromJson<LoginResp>(json);
            if (string.IsNullOrEmpty(resp?.token)) return false;
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(PToken, resp.token);
                LvnKeep.Put(PUser, resp.user_id);
                if (!string.IsNullOrEmpty(resp.name)) LvnKeep.Put(PName, resp.name);
            }
            // Cross-device recovery may have switched ACCOUNTS on this device —
            // the previous user's offline wallet must not leak into this one.
            LvnWallet.NoteUser(resp.user_id);
            SignedInChanged?.Invoke(resp.user_id);
            return true;
        }

        /// <summary>Attach a platform identity to the current account (POST
        /// /v1/auth/link) so it becomes recoverable from any device.</summary>
        public static async Task<LvnPlatformAuth.LinkResult> LinkProviderAsync(string provider, string token)
        {
            var body = JsonUtility.ToJson(new ProviderReq { provider = provider, token = token });
            var (code, _) = await PostAsync("/v1/auth/link", body);
            if (Ok(code)) return LvnPlatformAuth.LinkResult.Linked;
            if (code == 409) return LvnPlatformAuth.LinkResult.Conflict;
            return LvnPlatformAuth.LinkResult.Failed;
        }

        [Serializable] private class ProviderReq { public string provider; public string token; }
        [Serializable] private class LoginResp { public string user_id; public string token; public string name; }

        /// <summary>«Удалить аккаунт» (стор-требование): сервер стирает учётку,
        /// кошелёк и сейвы; локально сбрасываются токен, имя И device-секрет —
        /// иначе следующий /v1/auth/register с тем же device_id завёл бы
        /// «тот же» аккаунт заново, а игрок просил забыть его совсем.
        /// false = сервер недоступен или отказал; локально ничего не трогаем.</summary>
        public static async Task<bool> DeleteAccountAsync()
        {
            var (code, _) = await PostAsync("/v1/account/delete", "{\"confirm\":\"DELETE\"}");
            if (!Ok(code)) return false;
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(PToken);
                LvnKeep.Drop(PUser);
                LvnKeep.Drop(PName);
                LvnKeep.Drop(PDevice);
            }
            LvnWallet.ForgetLocal(); // офлайн-кошелёк не должен пережить владельца
            SignedInChanged?.Invoke("");
            return true;
        }

        /// <summary>
        /// ЧТО ЗНАЧИТ ОТВЕТ — три вопроса, на которые нельзя отвечать по месту.
        ///
        /// <para>Тридцать вызовов разбирали код состояния сами, и правил
        /// набралось три: «успех — это ровно 200» (двадцать семь мест), «успех —
        /// класс 2xx» (два), «а ноль — это оффлайн» (одно). Сервер сегодня
        /// отвечает двумястами почти везде, поэтому разнобой не виден — но
        /// первый же обработчик, вернувший 201 или 204, окажется ошибкой в
        /// двадцати семи местах из тридцати.</para>
        ///
        /// <para>Оффлайн отделён намеренно: ноль означает, что запрос НЕ ДОШЁЛ.
        /// Трактовать его как отказ сервера значит показать игроку «сервис
        /// недоступен» вместо «нет сети» и, хуже, выбросить накопленное — очередь
        /// событий, неотправленную покупку, — которое надо было СОХРАНИТЬ до
        /// возвращения связи.</para>
        /// </summary>
        /// <summary>УДАЧНЫЙ ОТВЕТ — одно правило на всех: весь второй разряд,
        /// а не «ровно 200». Дом правила сам же его и нарушал в шести местах, и
        /// это не мелочь: сервер вправе ответить 201 или 204 (а прокси —
        /// нормализовать), и тогда удача читалась бы как отказ. Дороже всего
        /// это стоило бы привязке аккаунта: «уже привязан» вернулось бы игроку
        /// как «не вышло».</summary>
        public static bool Ok(long code) => code >= 200 && code < 300;

        /// <summary>Запрос не дошёл вовсе: сети нет, сервер не отвечает, DNS
        /// молчит. Не то же самое, что отказ сервера — см. <see cref="Ok"/>.</summary>
        public static bool Offline(long code) => code == 0;

        /// <summary>
        /// ОТВЕТ, ПРИГОДНЫЙ К ЧТЕНИЮ, — или ничего.
        ///
        /// <para>Ответ читают, только если он ВЕСЬ в порядке: код успешный,
        /// тело есть и это разбираемый JSON. Разнобой хоть в одном из трёх
        /// разбирают уже как данные — и падают на пустой строке или молча берут
        /// поля из тела ошибки.</para>
        ///
        /// <para>Три проверки стояли врозь в пяти службах: код с телом в одном
        /// условии, разбор — в собственном try у каждой. Здесь они одно
        /// действие: null значит «читать нечего», и звонящий возвращает свой
        /// пустой ответ.</para>
        /// </summary>
        public static JObject Json(long code, string body)
        {
            if (!Ok(code) || string.IsNullOrEmpty(body)) return null;
            try { return JObject.Parse(body); } catch { return null; }
        }

        /// <summary>POST json; returns (status, body). 0 = transport error
        /// (offline). Attaches the bearer token unless auth=false.</summary>
        public static Task<(long code, string body)> PostAsync(string path, string json, bool auth = true)
            => SendAsync("POST", path, json, auth);

        /// <summary>
        /// ОДИН ЗАПРОС НА ВСЕ СЛУЖБЫ: адрес, токен, терпение, ожидание ответа и
        /// правило «транспорт не дошёл» (код 0).
        ///
        /// <para>Тел было два — почти одинаковых, отличавшихся глаголом и телом
        /// письма. Разошлись они уже: заголовок авторизации POST ставил по
        /// параметру, GET — всегда; добавить общий заголовок или заменить
        /// правило отказа значило бы вспомнить про оба.</para>
        /// </summary>
        private static async Task<(long code, string body)> SendAsync(string method, string path, string json, bool auth)
        {
            if (string.IsNullOrEmpty(BaseUrl)) return (0, null);
            using var req = new UnityWebRequest(BaseUrl + path, method);
            if (json != null || method == "POST")
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            if (auth && SignedIn) req.SetRequestHeader("Authorization", "Bearer " + Token);
            req.timeout = Lvn.LvnNetPatience.RequestSeconds;
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            bool reached = req.result == UnityWebRequest.Result.Success || req.responseCode != 0;
            // СВЯЗЬ — ФАКТ, А НЕ МНЕНИЕ, и знает его тот, кто только что ходил
            // на сервер. Продуктовые службы ходят на ТОТ ЖЕ адрес, что и
            // контент, значит их ответ говорит о связи ровно то же. Раньше
            // здесь стоял ШОВ — делегат, который ставила оболочка, потому что
            // дом признака жил в чужой сборке; не поставлен — службы молчали.
            // Дом переехал в ядро, и шов вместе с кварталом ушёл.
            var why = "services " + method + " " + path;
            if (reached) Lvn.LvnNetworkStatus.MarkOnline(why);
            else Lvn.LvnNetworkStatus.MarkOffline(why);
            if (!reached) return (0, null);
            return (req.responseCode, req.downloadHandler.text);
        }

        [Serializable] private class MeResp { public string user_id; public string[] providers; }

        /// <summary>The platform providers this account is linked to
        /// (<c>"google"</c>, <c>"apple"</c>); empty for a device-only account,
        /// null when offline. The settings screen shows "signed in via …" from
        /// this (GET /v1/auth/me).</summary>
        public static async Task<string[]> GetProvidersAsync()
        {
            var (code, json) = await GetAsync("/v1/auth/me");
            if (!Ok(code) || string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<MeResp>(json)?.providers ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>GET json with the bearer token; same contract as PostAsync.</summary>
        public static Task<(long code, string body)> GetAsync(string path)
            => SendAsync("GET", path, null, auth: true);
    }
}
