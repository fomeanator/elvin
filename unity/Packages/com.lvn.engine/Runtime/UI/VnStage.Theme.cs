using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ПОСТАНОВКА НОВЕЛЛЫ НА ЖИВОЙ СЦЕНЕ — как она перекрашивает то, что уже
    /// стоит на экране.
    ///
    /// <para>Новелла приносит свою тему (`ui.stage`, `ui.dialogue`,
    /// `ui.choices`): цвета, метрику окна реплики, кадр актёра, переходы,
    /// картинки рамок. Собрать всё это при рождении сцены — половина дела;
    /// вторая половина в том, что тема МЕНЯЕТСЯ на ходу — новая глава, другая
    /// новелла, смена настроек, — и перекраска обязана дойти до каждого уже
    /// созданного элемента, не разобрав кадр.</para>
    ///
    /// <para>Это отдельная тема от того, как сцена СОБИРАЕТСЯ и живёт:
    /// подъём, кадр, ввод и уборка — в основном файле. Сюда приходят с
    /// вопросом «а как это должно выглядеть у ЭТОЙ новеллы».</para>
    /// </summary>
    public sealed partial class VnStage
    {
        /// <summary>Replace the visual theme. If the stage is already built, the
        /// dialogue box and choice list are recreated with the new look — so a
        /// manifest-driven theme (<see cref="VnThemeBuilder"/>) can be applied
        /// after construction. Call before the first chapter plays.</summary>
        public void ApplyTheme(VnTheme theme)
        {
            Theme = theme ?? new VnTheme();
            if (!_built) return; // Build() will pick up the new Theme
            RebuildChrome();
            // Resolve any manifest-driven background-image urls to sprites, then
            // rebuild once more so the dialogue/choices show their skinned panels.
            LvnAsync.Fire(EnsureThemeImagesAsync(), "EnsureThemeImages");
        }
        // The default backdrop behind the canvas scene: a seamless texture tiled
        // as a fine grid, so letterboxed scenes (a width-fit Spine leaves bars)
        // sit on a pattern instead of flat black. Overridden by any real `bg`.
        private async System.Threading.Tasks.Task ApplyDefaultBackdropAsync(World.WorldStage scene)
        {
            if (Assets == null || scene == null) return;
            try
            {
                var spr = await Assets.LoadSpriteAsync(LvnAssetPath.Under("ui/tile-bg.jpg"), _cts.Token);
                if (spr != null && spr.texture != null) scene.Background.SetTile(spr.texture, 140f);
            }
            catch { }   // разбор темы главы: кривое поле не повод не начать главу
        }
        // Recreate the dialogue box and choice list from the current Theme, keeping
        // their z-order (…, dialogue, choices, fx). Used by ApplyTheme and after the
        // theme's background images finish loading.
        private void RebuildChrome()
        {
            var root = GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null || _fx == null) return;

            if (_choices != null)
            {
                _choices.OnSelected -= OnChoiceSelected;
                _choices.VisibleChanged -= OnChoicesVisibleChanged;
                _choices.RemoveFromHierarchy();
            }
            if (_dialogue != null) _dialogue.RemoveFromHierarchy();
            // The shared window wears the theme too — drop it so the next use
            // rebuilds it with the fresh skin. NEVER while it's open: a live
            // re-theme (content sync) during an in-story wardrobe would orphan
            // the hosted content, its await would never resolve, and the held
            // script would soft-lock. An open window just keeps the old skin
            // until it closes.
            if (_panelHost != null && !_panelHost.IsOpen)
            { _panelHost.RemoveFromHierarchy(); _panelHost = null; }

            ResolveFont();
            _dialogue = new DialogueBox(Theme);
            _dialogue.RevealingChanged += OnDialogueRevealing; // луп клавиатуры
            _dialogue.SetUserOpacity(LvnPrefs.DialogOpacity);
            SetSayVisible(_sayUp); // a re-theme between lines must not reveal the empty frame
            _choices = new ChoiceList(Theme);
            // Rebuilt chrome goes back into the safe-area container, before the
            // label layer — keeps z-order: dialogue, choices, labels (fx above).
            var chromeHost = (VisualElement)_chromeSafe ?? root;
            int labelIndex = _labelLayer != null ? chromeHost.IndexOf(_labelLayer) : -1;
            if (labelIndex < 0) labelIndex = chromeHost.childCount;
            chromeHost.Insert(labelIndex, _dialogue);
            chromeHost.Insert(labelIndex + 1, _choices);
            _choices.OnSelected += OnChoiceSelected;
            _choices.VisibleChanged += OnChoicesVisibleChanged;
            // RebuildChrome replaces the VisualElements themselves. Event
            // subscriptions belong to those instances and must be wired again.
            WireChoiceGeometrySync();

            // The quick menu is themeable too (manifest.ui.menu) — rebuild it with
            // the fresh theme, keeping it the topmost layer.
            _menu?.Close();
            _menu?.RemoveFromHierarchy();
            _menu = new StageMenu(this, Theme);
            ((VisualElement)_menuSafe ?? root).Add(_menu);

            // Restore the visible beat onto the fresh chrome so a live theme change
            // never blanks the line/choices the player is mid-reading (the text is
            // set instantly — no typewriter restart on each live tweak).
            if (_sayUp && _backlog.Count > 0)
            {
                var beat = _backlog[_backlog.Count - 1];
                _dialogue.SetSpeaker(beat.who, DialogueSideForCurrentSpeaker(beat.who));
                _dialogue.ApplyStyle(beat.style);
                _dialogue.SetText(beat.text);
            }
            if (_curChoices != null) _choices.Present(_curChoices);
        }
        // Load the theme's background-image urls (panel/nameplate/choice buttons)
        // through ILvnAssets and assign the resolved sprites onto the Theme, then
        // rebuild the chrome so they show. Each url loads at most once (skipped when
        // the sprite is already set), so this is safe to call after every ApplyTheme.
        private async Task EnsureThemeImagesAsync()
        {
            if (Theme == null || Assets == null || _cts == null) return;

            async Task<bool> Resolve(string url, System.Action<Sprite> assign)
            {
                if (string.IsNullOrEmpty(url)) return false;
                var sprite = await Assets.LoadSpriteAsync(url, _cts.Token);
                if (sprite == null) return false;
                assign(sprite);
                return true;
            }

            bool any = false;
            if (Theme.PanelSprite == null) any |= await Resolve(Theme.PanelImageUrl, s => Theme.PanelSprite = s);
            if (Theme.PlateSprite == null) any |= await Resolve(Theme.PlateImageUrl, s => Theme.PlateSprite = s);
            if (Theme.ChoiceSprite == null) any |= await Resolve(Theme.ChoiceImageUrl, s => Theme.ChoiceSprite = s);
            if (Theme.ChoiceHoverSprite == null) any |= await Resolve(Theme.ChoiceHoverImageUrl, s => Theme.ChoiceHoverSprite = s);

            if (any && _built) RebuildChrome();

            // ЗВУКИ ИНТЕРФЕЙСА (manifest ui.sounds) едут тем же ленивым путём:
            // перестраивать оформление под них не нужно, места воспроизведения
            // читают поля напрямую. Хост без звука просто молчит.
            async Task Clip(string url, System.Action<AudioClip> assign)
            {
                if (string.IsNullOrEmpty(url)) return;
                try { assign(await Assets.LoadAudioAsync(url, _cts.Token)); }
                catch { /* хост может не везти звук вовсе — это не ошибка главы */ }
            }
            if (_sndClick == null) await Clip(Theme.ClickSoundUrl, c => _sndClick = c);
            if (_sndChoice == null) await Clip(Theme.ChoiceSoundUrl, c => _sndChoice = c);
            if (_sndType == null) await Clip(Theme.TypeSoundUrl, c => _sndType = c);
        }
    }
}
