using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// A chain of <see cref="ILvnAssets"/> loaders tried in order. The first
    /// loader that returns a non-null result wins, and the result is cached
    /// by the winning loader (or an optional shared cache layer).
    ///
    /// Typical setup:
    /// <code>
    ///   var assets = new ChainAssets()
    ///       .Add(new MemoryCache())        // L1: fastest
    ///       .Add(new DirectoryAssets(dir)) // L2: local disk
    ///       .Add(new AddressablesAssets()) // L3: Unity bundles
    ///       .Add(new NetworkAssets(cdn));  // L4: HTTP fallback
    /// </code>
    /// </summary>
    public sealed class ChainAssets : ILvnAssets
    {
        private readonly List<ILvnAssets> _chain = new List<ILvnAssets>();

        /// <summary>Add a loader to the end of the chain. Returns this for
        /// fluent configuration.</summary>
        public ChainAssets Add(ILvnAssets loader)
        {
            if (loader != null) _chain.Add(loader);
            return this;
        }

                /// <summary>ПЕРВЫЙ, КТО ОТВЕТИЛ, — ТОТ И ОТВЕЧАЕТ.
        ///
        /// <para>Обход цепочки стоял ТРЕМЯ копиями по восемь строк — картинка,
        /// текст, звук, — и различались они только тем, о чём спрашивают и что
        /// считать ответом. Цепочка — это механизм: спросить по порядку и
        /// остановиться на первом, кто дал ответ. Вид содержимого к нему
        /// отношения не имеет.</para>
        ///
        /// <para>Признак ответа остался доводом, потому что он и вправду
        /// разный: для картинки и звука ответ — не <c>null</c>, для текста
        /// пустая строка тоже «нечего дать».</para></summary>
        private async Task<T> FirstAnswerAsync<T>(System.Func<ILvnAssets, Task<T>> ask,
                                                  System.Func<T, bool> answered)
        {
            foreach (var loader in _chain)
            {
                var got = await ask(loader);
                if (answered(got)) return got;
            }
            return default;
        }

        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            => FirstAnswerAsync(l => l.LoadSpriteAsync(url, ct), s => s != null);

        public Task<string> LoadTextAsync(string url, CancellationToken ct)
            => FirstAnswerAsync(l => l.LoadTextAsync(url, ct), t => !string.IsNullOrEmpty(t));

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            => FirstAnswerAsync(l => l.LoadAudioAsync(url, ct), c => c != null);

        
        
        public void Unload(string url)
        {
            foreach (var loader in _chain)
                loader.Unload(url);
        }

        public void UnloadAll()
        {
            foreach (var loader in _chain)
                loader.UnloadAll();
        }
    }
}
