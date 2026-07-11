using System;
using System.Threading.Tasks;

namespace Lvn.Services
{
    /// <summary>
    /// The seam between the engine and the PLATFORM sign-in SDKs (Google Play
    /// Games / Sign in with Apple / anything OAuth-shaped). The engine ships
    /// no store SDKs — the host installs its plugin and plugs one delegate per
    /// provider; the auth screen then shows that provider's button and the
    /// server does the actual token verification (Google tokeninfo / Apple
    /// JWKS). Without any providers the flow stays what it was: the silent
    /// device account.
    /// </summary>
    public static class LvnPlatformAuth
    {
        /// <summary>One platform's sign-in: runs the SDK's interactive flow
        /// and returns the identity token to hand the server (Google: the
        /// id_token; Apple: the identityToken). Null = cancelled/failed.</summary>
        public delegate Task<string> TokenFlow();

        /// <summary>Google Play Games / Google Sign-In. Set by the host at
        /// boot: <c>LvnPlatformAuth.Google = async () => …sdk…;</c></summary>
        public static TokenFlow Google;

        /// <summary>Sign in with Apple.</summary>
        public static TokenFlow Apple;

        /// <summary>Editor/test builds: a fake provider the server accepts
        /// under -auth-dev. The token doubles as the stable subject, so a
        /// fixed string recovers the same test account every run.</summary>
        public static TokenFlow Dev;

        public static bool Has(string provider) => Flow(provider) != null;

        public static TokenFlow Flow(string provider) => provider switch
        {
            "google" => Google,
            "apple" => Apple,
            "dev" => Dev,
            _ => null,
        };

        /// <summary>Run a provider's flow and SIGN IN through the backend —
        /// the cross-device recovery path (a known identity returns its
        /// account, wallet and saves included; an unknown one becomes a new
        /// account). Returns false when cancelled/offline/rejected.</summary>
        public static async Task<bool> SignInAsync(string provider)
        {
            var flow = Flow(provider);
            if (flow == null) return false;
            string token;
            try { token = await flow(); }
            catch { return false; }
            if (string.IsNullOrEmpty(token)) return false;
            return await LvnBackend.LoginWithProviderAsync(provider, token);
        }

        /// <summary>Run a provider's flow and LINK the identity to the current
        /// device account (so it becomes recoverable). "conflict" in the result
        /// means the identity already belongs to another account — the caller
        /// may offer switching via <see cref="SignInAsync"/>.</summary>
        public static async Task<LinkResult> LinkAsync(string provider)
        {
            var flow = Flow(provider);
            if (flow == null) return LinkResult.Failed;
            string token;
            try { token = await flow(); }
            catch { return LinkResult.Failed; }
            if (string.IsNullOrEmpty(token)) return LinkResult.Failed;
            return await LvnBackend.LinkProviderAsync(provider, token);
        }

        public enum LinkResult { Linked, Conflict, Failed }
    }
}
