using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>A loaded 3D set plus the lease that keeps its AssetBundle alive.
    /// The stage owns the lease until the instantiated set is replaced.</summary>
    public sealed class Lvn3DSetAsset : IDisposable
    {
        private Action _release;

        public GameObject Prefab { get; }
        public string Id { get; }
        public bool Remote { get; }

        public Lvn3DSetAsset(string id, GameObject prefab, bool remote = false,
            Action release = null)
        {
            Id = id;
            Prefab = prefab;
            Remote = remote;
            _release = release;
        }

        public void Dispose()
        {
            var release = Interlocked.Exchange(ref _release, null);
            release?.Invoke();
        }
    }

    /// <summary>
    /// The asset-loading seam: how the stage turns a command's <c>sprite_url</c>
    /// into a <see cref="Sprite"/>. The engine ships no loader so it stays
    /// agnostic — plug in Resources, Addressables, a file reader, or a network
    /// cache. Leave <see cref="VnStage.Assets"/> null to run with solid-colour
    /// backgrounds and no character art (handy for greyboxing a script).
    ///
    /// Implementors should cache by url so repeated loads are instant.
    /// Off-main-thread I/O is strongly recommended to avoid freezing the click
    /// → Advance loop.
    /// </summary>
    public interface ILvnAssets
    {
        /// <summary>Load a single sprite by url. Return null on failure.</summary>
        Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct);

        /// <summary>Resolve an <c>audio</c> command's url to a clip. Return null
        /// (or throw) if you don't ship audio — the stage just stays silent.</summary>
        Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct);

        /// <summary>Load a text asset (a Spine skeleton json / atlas). Default:
        /// unsupported (null) — implemented by the network and directory loaders.</summary>
        Task<string> LoadTextAsync(string url, CancellationToken ct) => Task.FromResult<string>(null);

        /// <summary>Load a 3D set — the prefab a `bg3d` op stands behind the scene
        /// so the script can move a camera through it instead of asking for
        /// another painted angle. Default: unsupported (null), and the stage then
        /// simply keeps the flat background; a game that ships 3D sets implements
        /// this over Resources or Addressables.</summary>
        Task<GameObject> LoadPrefabAsync(string url, CancellationToken ct) => Task.FromResult<GameObject>(null);

        /// <summary>Load a leased 3D set. Bundle-backed loaders override this so
        /// set changes release CPU/GPU memory. The default keeps older custom
        /// prefab loaders source-compatible.</summary>
        async Task<Lvn3DSetAsset> Load3DSetAsync(string id, CancellationToken ct)
        {
            var prefab = await LoadPrefabAsync(id, ct);
            return prefab != null ? new Lvn3DSetAsset(id, prefab) : null;
        }

        /// <summary>Warm a 3D set without instantiating it. Bundle-backed loaders
        /// keep a bounded ready-to-use bundle pool; the compatibility default at
        /// least exercises the loader/cache and immediately releases its lease.</summary>
        async Task Preload3DSetAsync(string id, CancellationToken ct)
        {
            using var loaded = await Load3DSetAsync(id, ct);
        }

        /// <summary>Speculative batch load: warm the cache for upcoming urls.
        /// Default implementation calls <see cref="LoadSpriteAsync"/> for each
        /// sprite-kind url and <see cref="LoadAudioAsync"/> for audio-kind urls.
        /// Override for parallel loading (Addressables, UnityWebRequest, etc.).</summary>
        Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            // Пустой список — обычное дело (глава без звука, набор без обложек),
            // и он не повод будить планировщик задач.
            if (urls == null || urls.Count == 0) return Task.CompletedTask;
            var tasks = new List<Task>();
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(kind == "audio"
                    ? LoadAudioAsync(url, ct).ContinueWith(_ => { })
                    : LoadSpriteAsync(url, ct).ContinueWith(_ => { }));
            }
            return Task.WhenAll(tasks);
        }

        /// <summary>Release the cached asset for a single url. Safe to call if
        /// the url was never loaded. Implementors should destroy the underlying
        /// Unity Object (Texture2D, AudioClip) to free GPU/CPU memory.</summary>
        void Unload(string url);

        /// <summary>Release all cached assets. Call on scene transition or
        /// application exit to avoid leaked textures.</summary>
        void UnloadAll();
    }
}
