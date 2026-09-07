using System;
using Lvn.Content;

namespace Lvn.UI.Screens
{
    internal enum ManifestFailureKind { Network, AppError }

    /// <summary>Причина отказа определяет сообщение, уровень лога и паузу повтора.</summary>
    internal static class ManifestRecovery
    {
        public static ManifestFailureKind KindFor(Exception error)
            => error is LvnFetchException ? ManifestFailureKind.Network : ManifestFailureKind.AppError;

        public static float PauseSeconds(ManifestFailureKind kind, int attempt)
            => kind == ManifestFailureKind.Network ? 5f : LvnBackoff.DelaySeconds(attempt + 1);
    }
}
