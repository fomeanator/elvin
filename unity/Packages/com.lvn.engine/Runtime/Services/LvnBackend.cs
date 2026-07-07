using System;
using System.Text;
using System.Threading.Tasks;
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

        public static string UserId => PlayerPrefs.GetString(PUser, "");
        public static string Token => PlayerPrefs.GetString(PToken, "");
        public static bool SignedIn => !string.IsNullOrEmpty(Token);

        /// <summary>Raised after a successful (re-)registration.</summary>
        public static event Action<string> SignedInChanged;

        /// <summary>Register (or recover) the device account. Safe to call every
        /// boot; no-ops offline and keeps the previous token.</summary>
        public static async Task<bool> EnsureRegisteredAsync()
        {
            if (string.IsNullOrEmpty(BaseUrl)) return SignedIn;
            var device = PlayerPrefs.GetString(PDevice, "");
            if (string.IsNullOrEmpty(device))
            {
                device = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PDevice, device);
                PlayerPrefs.Save();
            }
            var body = JsonUtility.ToJson(new RegisterReq { device_id = device });
            var (code, json) = await PostAsync("/v1/auth/register", body, auth: false);
            if (code != 200 || string.IsNullOrEmpty(json)) return SignedIn;
            var resp = JsonUtility.FromJson<RegisterResp>(json);
            if (string.IsNullOrEmpty(resp?.token)) return SignedIn;
            PlayerPrefs.SetString(PToken, resp.token);
            PlayerPrefs.SetString(PUser, resp.user_id);
            PlayerPrefs.Save();
            LvnWallet.NoteUser(resp.user_id); // bind (or reset) the offline wallet to this account
            SignedInChanged?.Invoke(resp.user_id);
            return true;
        }

        [Serializable] private class RegisterReq { public string device_id; }
        [Serializable] private class RegisterResp { public string user_id; public string token; }

        /// <summary>The profile display name — local-first (kept in PlayerPrefs
        /// even offline), synced to the account when a server is reachable.</summary>
        public static string DisplayName => PlayerPrefs.GetString(PName, "");

        /// <summary>Save the display name locally and push it to the account
        /// (POST /v1/auth/profile). Offline the local copy still sticks — the
        /// next successful call syncs it.</summary>
        public static async Task<bool> SetDisplayNameAsync(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            PlayerPrefs.SetString(PName, name);
            PlayerPrefs.Save();
            var (code, _) = await PostAsync("/v1/auth/profile", JsonUtility.ToJson(new ProfileReq { name = name }));
            return code == 200;
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
            if (code != 200 || string.IsNullOrEmpty(json)) return false;
            var resp = JsonUtility.FromJson<LoginResp>(json);
            if (string.IsNullOrEmpty(resp?.token)) return false;
            PlayerPrefs.SetString(PToken, resp.token);
            PlayerPrefs.SetString(PUser, resp.user_id);
            if (!string.IsNullOrEmpty(resp.name)) PlayerPrefs.SetString(PName, resp.name);
            PlayerPrefs.Save();
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
            if (code == 200) return LvnPlatformAuth.LinkResult.Linked;
            if (code == 409) return LvnPlatformAuth.LinkResult.Conflict;
            return LvnPlatformAuth.LinkResult.Failed;
        }

        [Serializable] private class ProviderReq { public string provider; public string token; }
        [Serializable] private class LoginResp { public string user_id; public string token; public string name; }

        /// <summary>POST json; returns (status, body). 0 = transport error
        /// (offline). Attaches the bearer token unless auth=false.</summary>
        public static async Task<(long code, string body)> PostAsync(string path, string json, bool auth = true)
        {
            if (string.IsNullOrEmpty(BaseUrl)) return (0, null);
            using var req = new UnityWebRequest(BaseUrl + path, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (auth && SignedIn) req.SetRequestHeader("Authorization", "Bearer " + Token);
            req.timeout = 10;
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success && req.responseCode == 0) return (0, null);
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
            if (code != 200 || string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<MeResp>(json)?.providers ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>GET json with the bearer token; same contract as PostAsync.</summary>
        public static async Task<(long code, string body)> GetAsync(string path)
        {
            if (string.IsNullOrEmpty(BaseUrl)) return (0, null);
            using var req = UnityWebRequest.Get(BaseUrl + path);
            if (SignedIn) req.SetRequestHeader("Authorization", "Bearer " + Token);
            req.timeout = 10;
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success && req.responseCode == 0) return (0, null);
            return (req.responseCode, req.downloadHandler.text);
        }
    }
}
