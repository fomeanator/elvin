using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lvn.Content
{
    public sealed partial class ContentLoader
    {
        private readonly object _offlinePinsLock = new object();
        private HashSet<string> _offlinePins;
        private string OfflinePinsPath => Path.Combine(_cacheRoot, "offline-pins.json");

        /// <summary>Whether the published version index declares an optional
        /// file (for example a chapter's localization sidecar).</summary>
        public bool HasPublishedAsset(string url) => VersionFor(url) != null;

        private HashSet<string> ReadOfflinePins()
        {
            if (_offlinePins != null) return _offlinePins;
            foreach (var path in new[] { OfflinePinsPath, OfflinePinsPath + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    _offlinePins = new HashSet<string>();
                    foreach (var key in JArray.Parse(File.ReadAllText(path)))
                        if (key.Type == JTokenType.String) _offlinePins.Add((string)key);
                    return _offlinePins;
                }
                catch { _offlinePins = null; }
            }
            if (File.Exists(OfflinePinsPath) || File.Exists(OfflinePinsPath + ".bak"))
                throw new IOException("offline pins unreadable; automatic cache deletion suspended");
            return _offlinePins = new HashSet<string>();
        }

        /// <summary>Explicit downloads are retained even over the streaming
        /// quota. Record exact version keys BEFORE downloading, so a sweep
        /// cannot evict the beginning of a still-running large download.</summary>
        public void PinOfflineAssets(IEnumerable<string> urls)
        {
            lock (_offlinePinsLock)
            {
                var pins = new HashSet<string>(ReadOfflinePins());
                foreach (var url in urls) AddLiveKeysFor(url, pins);
                var json = new JArray(pins).ToString(Formatting.None);
                AtomicWriteAllText(OfflinePinsPath, json);
                AtomicWriteAllText(OfflinePinsPath + ".bak", json);
                _offlinePins = pins;
            }
        }

        private void AddOfflinePins(HashSet<string> live, HashSet<string> protect)
        {
            lock (_offlinePinsLock)
            {
                var pins = ReadOfflinePins();
                live.UnionWith(pins);
                protect.UnionWith(pins);
            }
        }

        private bool IsOfflinePinned(string url)
        {
            lock (_offlinePinsLock)
                return ReadOfflinePins().Contains(HashKey(url, VersionFor(url)));
        }

        private void ClearOfflinePins()
        {
            lock (_offlinePinsLock)
            {
                // Explicit removal is allowed even when the old index is corrupt.
                AtomicWriteAllText(OfflinePinsPath, "[]");
                AtomicWriteAllText(OfflinePinsPath + ".bak", "[]");
                _offlinePins = new HashSet<string>();
            }
        }
    }
}
