using System.Threading;
using UnityEngine;

namespace Lvn.Services
{
    public static partial class LvnWallet
    {
        private static float _nextSync;
        private static int _retrySeconds = 1;

        private static void WakeSync()
        {
            _nextSync = 0;
            WalletSyncRunner.Ensure();
        }

        private static void ScheduleRetry()
        {
            _nextSync = LvnClock.Wall() + _retrySeconds;
            _retrySeconds = _queue.Count == 0 ? 1 : System.Math.Min(30, _retrySeconds * 2);
        }

        private sealed class WalletSyncRunner : MonoBehaviour
        {
            private static WalletSyncRunner _instance;
            private static int _wake;

            static WalletSyncRunner()
            {
                LvnNetworkStatus.Changed += online => { if (online) Interlocked.Exchange(ref _wake, 1); };
            }

            internal static void Ensure()
            {
                if (_instance != null || !Application.isPlaying) return;
                var go = new GameObject("LvnWalletSync") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<WalletSyncRunner>();
            }

            private void Update()
            {
                if (Interlocked.Exchange(ref _wake, 0) != 0) { _nextSync = 0; _retrySeconds = 1; }
                if (!_loaded || _queue.Count == 0 || LvnNetworkStatus.ForceOffline
                    || string.IsNullOrEmpty(LvnBackend.BaseUrl) || !LvnBackend.SignedIn
                    || (_flush != null && !_flush.IsCompleted) || LvnClock.Wall() < _nextSync) return;
                LvnAsync.Fire(FlushAsync(), "WalletSync");
            }

            private void OnApplicationPause(bool paused) { if (!paused) Interlocked.Exchange(ref _wake, 1); }
        }
    }
}
