using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.UI;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАГРУЗЧИК-ПУСТЫШКА для экранов, которым ассеты не нужны.
    ///
    /// <para>Шесть тестов держали по своей копии одного и того же класса — слово в
    /// слово. Пока копий шесть, расширение <see cref="ILvnAssets"/> ломает сборку
    /// тестов в трёх местах сразу, и каждое чинится одинаково: это не проверка,
    /// а дань интерфейсу.</para>
    ///
    /// <para>Отдаёт <c>null</c> на всё намеренно: экран обязан пережить
    /// отсутствие картинки и звука — ровно это тесты и проверяют, а не то, как
    /// он рисует настоящий арт.</para>
    /// </summary>
    public sealed class TestAssets : ILvnAssets
    {
        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
        public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
        public void Unload(string url) { }
        public void UnloadAll() { }
    }
}
