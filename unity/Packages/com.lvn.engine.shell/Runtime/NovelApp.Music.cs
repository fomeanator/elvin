using System;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// МУЗЫКА МЕНЮ — часть <see cref="NovelApp"/>: трек, который играет везде,
    /// кроме самой новеллы, глохнет на время главы и возвращается после.
    ///
    /// <para>Своим файлом, потому что это законченный сюжет со своими
    /// правилами (выбор трека игроком, громкость из настроек, кроссфейд при
    /// смене) и трогают его отдельно от всего остального.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // ── музыка меню (ui.browse.music) ────────────────────────────────────
        private AudioSource _menuMusic;

        private async Task StartMenuMusicAsync(string url)
        {
            try
            {
                var clip = await _assets.Loader.DownloadAudioClipAsync(url, _quitting);
                if (clip == null) return;
                _menuMusic = gameObject.AddComponent<AudioSource>();
                _menuMusic.clip = clip;
                _menuMusic.loop = true;
                _menuMusic.playOnAwake = false;
                _menuMusic.volume = Lvn.UI.LvnVolumes.Of(Lvn.UI.LvnVolumes.Music); // тумблер и ползунок ведут и меню
                _leash.Hold(() => Lvn.UI.LvnPrefs.Changed += SyncMenuMusicVolume,
                            () => Lvn.UI.LvnPrefs.Changed -= SyncMenuMusicVolume);
                if (!InChapter) _menuMusic.Play();
            }
            catch (Exception ex) { Debug.LogWarning($"[lvn-app] музыка меню: {ex.Message}"); }
        }

        // Трек меню: выбранный игроком из ui.browse.music_options, иначе базовый.
        private static string ResolveMenuTrackUrl(LvnManifest manifest)
        {
            var b = manifest?.ui?.browse;
            var picked = Lvn.UI.LvnPrefs.MenuTrack;
            if (!string.IsNullOrEmpty(picked) && b?.music_options != null)
                foreach (var o in b.music_options)
                    if (o != null && o.id == picked && !string.IsNullOrEmpty(o.url))
                        return o.url;
            return b?.music;
        }

        // Смена трека из настроек: перезагрузить клип на лету.
        private async Task SwitchMenuTrackAsync(string url)
        {
            if (_menuMusic == null || string.IsNullOrEmpty(url)) return;
            try
            {
                var clip = await _assets.Loader.DownloadAudioClipAsync(url, _quitting);
                if (clip == null || _menuMusic == null) return;
                bool was = _menuMusic.isPlaying;
                _menuMusic.Stop();
                _menuMusic.clip = clip;
                if (was && !InChapter) _menuMusic.Play();
            }
            catch (OperationCanceledException) { }   // приложение закрывают — не отказ
            catch (Exception ex) { Debug.LogWarning($"[lvn-app] смена трека меню: {ex.Message}"); }
        }

        private void SyncMenuMusicVolume()
        {
            // Мастер-тумблер «Все звуки» обязан глушить и музыку меню: она
            // живёт мимо StageAudio, и «выключаю звук — ничего не происходит»
            // (живой репорт) было именно про неё.
            if (_menuMusic != null)
                _menuMusic.volume = Lvn.UI.LvnVolumes.Of(Lvn.UI.LvnVolumes.Music);
        }
    }
}
