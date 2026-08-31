using System;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Player-facing preferences — text speed, auto-advance, per-channel volume,
    /// reduce-motion, dialogue window opacity. A single static store backed by
    /// PlayerPrefs: setters clamp, persist and raise <see cref="Changed"/>, so
    /// live consumers (StageAudio volumes, the dialogue box, the settings panel)
    /// stay in sync without polling. Game-agnostic — these are the player's
    /// device-level comfort settings, not per-title state.
    /// </summary>
    public static class LvnPrefs
    {
        /// <summary>Raised after any preference changes (already persisted).</summary>
        public static event Action Changed;

        private const string P = "lvn_pref_";

        // Backing fields, loaded once on first touch.
        private static bool _loaded;
        private static float _textSpeed, _autoDelayScale, _volMusic, _volAmbient, _volSfx, _volVoice, _dialogOpacity;
        private static bool _autoAdvance, _reduceMotion, _skipReadOnly;
        private static bool _soundOn = true;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _textSpeed = LvnKeep.Get(P + "text_speed", 1f);
            _autoAdvance = LvnKeep.Get(P + "auto_advance", 0) == 1;
            _autoDelayScale = LvnKeep.Get(P + "auto_delay", 1f);
            _volMusic = LvnKeep.Get(P + "vol_music", 1f);
            _volAmbient = LvnKeep.Get(P + "vol_ambient", 1f);
            _volSfx = LvnKeep.Get(P + "vol_sfx", 1f);
            _volVoice = LvnKeep.Get(P + "vol_voice", 1f);
            _reduceMotion = LvnKeep.Get(P + "reduce_motion", 0) == 1;
            _skipReadOnly = LvnKeep.Get(P + "skip_read_only", 0) == 1;
            _dialogOpacity = LvnKeep.Get(P + "dialog_opacity", 1f);
            _textScale = LvnKeep.Get(P + "text_scale", 1f);
            _uiScale = LvnKeep.Get(P + "ui_scale", 1f);
            _fontFamily = LvnKeep.Get(P + "font_family", "");
            _textWeight = LvnKeep.Get(P + "text_weight", 0f);
            _uiWeight = LvnKeep.Get(P + "ui_weight", 0f);
            _soundOn = LvnKeep.Get(P + "sound_on", 1) == 1;
            _locale = LvnKeep.Get(P + "locale", "");
            _artQuality = LvnKeep.Get(P + "art_quality", "");
            // Миграция со старого двухпозиционного флага «Экономия».
            if (_artQuality == "" && LvnKeep.Get(P + "art_eco", 0) == 1) _artQuality = "1k";
            _menuTrack = LvnKeep.Get(P + "menu_track", "");
            _menuFavorite = LvnKeep.Get(P + "menu_favorite", "");
            _targetFps = LvnKeep.Get(P + "target_fps", 60) == 30 ? 30 : 60;
            TypewriterClock.UserSpeedMultiplier = _textSpeed;
        }

        private static void Set(ref float field, string key, float value)
        {
            if (Mathf.Approximately(field, value)) return;
            field = value;
            LvnKeep.Put(P + key, value);
            Changed?.Invoke();
        }

        private static void Set(ref bool field, string key, bool value)
        {
            if (field == value) return;
            field = value;
            LvnKeep.Put(P + key, value ? 1 : 0);
            Changed?.Invoke();
        }

        private static void Set(ref int field, string key, int value)
        {
            if (field == value) return;
            field = value;
            LvnKeep.Put(P + key, value);
            Changed?.Invoke();
        }

        private static void Set(ref string field, string key, string value)
        {
            if (field == value) return;
            field = value;
            LvnKeep.Put(P + key, value);
            Changed?.Invoke();
        }

        /// <summary>Typewriter speed multiplier (0.25×–3×; 1 = author's pace).
        /// Pushed into <see cref="TypewriterClock.UserSpeedMultiplier"/>.</summary>
        /// <summary>The player's chosen display name — asked ONCE (at a novel's
        /// start), persisted forever. Empty until entered.</summary>
        public static string PlayerName
        {
            get => LvnKeep.Get(P + "player_name", "");
            set { LvnKeep.Put(P + "player_name", value ?? ""); Changed?.Invoke(); }
        }

        // ── ЗАПИСИ БЕЗ ОПОВЕЩЕНИЯ ────────────────────────────────────────────
        // Ниже — настройки, которые НАРОЧНО не поднимают Changed, и это не
        // забывчивость: их читают на следующем витке оболочки или при следующем
        // запуске, а живого экрана они не меняют. Событие настроек летит на
        // каждое присваивание — в том числе на каждый кадр перетаскивания
        // ползунка, — и подписчики на нём пересобирают сцену меню; будить их
        // ради флага «вводная пройдена» значит платить пересборкой за то, чего
        // никто не увидит. Отличать от общего Set, который оповещает всегда.

        /// <summary>Has the boot welcome/auth screen been shown already? It
        /// greets the player exactly once — never again on later launches.</summary>
        public static bool SeenWelcome
        {
            get => LvnKeep.Get(P + "seen_welcome", 0) == 1;
            set { LvnKeep.Put(P + "seen_welcome", value ? 1 : 0); }
        }

        /// <summary>Пройдена ли ВВОДНАЯ новелла (title с <c>type: "intro"</c>).
        /// Пока нет — приложение не показывает витрину вообще: игрок попадает
        /// прямо в неё, а меню открывается только после. Это воронка, а не
        /// сборник новелл: выбор из списка на первом экране требует от человека
        /// решения раньше, чем он понял, во что играет.</summary>
        public static bool IntroDone
        {
            get => LvnKeep.Get(P + "intro_done", 0) == 1;
            set { LvnKeep.Put(P + "intro_done", value ? 1 : 0); }
        }

        /// <summary>Player opted into picking the content server manually at
        /// boot (a CS 1.6-style server browser) instead of auto-connecting to
        /// the first known server that answers /healthz. Off by default — the
        /// picker only appears once the player has asked for it once.</summary>
        public static bool ManualServerSelect
        {
            get => LvnKeep.Get(P + "manual_server_select", 0) == 1;
            set { LvnKeep.Put(P + "manual_server_select", value ? 1 : 0); }
        }

        /// <summary>The last server URL the player picked or typed in the boot
        /// server browser — "" means "use the build's default". Persists so a
        /// self-hoster doesn't retype their URL on every launch.</summary>
        public static string ServerUrlOverride
        {
            get => LvnKeep.Get(P + "server_url_override", "");
            set { LvnKeep.Put(P + "server_url_override", value ?? ""); }
        }

        public static float TextSpeed
        {
            get { EnsureLoaded(); return _textSpeed; }
            set
            {
                EnsureLoaded();
                // Пределы — у КАТАЛОГА настроек: ползунок и зажим обязаны знать
                // один диапазон, иначе ручка «пружинит» назад без объяснения.
                var v = Mathf.Clamp(value, LvnSettingsCatalog.TextSpeedMin, LvnSettingsCatalog.TextSpeedMax);
                TypewriterClock.UserSpeedMultiplier = v;
                Set(ref _textSpeed, "text_speed", v);
            }
        }

        /// <summary>Hands-free reading: advance automatically once a line has
        /// finished revealing and its reading delay has passed.</summary>
        public static bool AutoAdvance
        {
            get { EnsureLoaded(); return _autoAdvance; }
            set { EnsureLoaded(); Set(ref _autoAdvance, "auto_advance", value); }
        }

        /// <summary>Auto-advance delay multiplier (0.5×–2.5×; 1 = default pace).</summary>
        public static float AutoDelayScale
        {
            get { EnsureLoaded(); return _autoDelayScale; }
            set { EnsureLoaded(); Set(ref _autoDelayScale, "auto_delay", Mathf.Clamp(value, LvnSettingsCatalog.AutoDelayMin, LvnSettingsCatalog.AutoDelayMax)); }
        }

        /// <summary>Music channel volume (0–1), multiplied onto authored volume.</summary>
        public static float VolMusic
        {
            get { EnsureLoaded(); return _volMusic; }
            set { EnsureLoaded(); Set(ref _volMusic, "vol_music", Mathf.Clamp01(value)); }
        }

        /// <summary>Ambient channel volume (0–1).</summary>
        public static float VolAmbient
        {
            get { EnsureLoaded(); return _volAmbient; }
            set { EnsureLoaded(); Set(ref _volAmbient, "vol_ambient", Mathf.Clamp01(value)); }
        }

        /// <summary>Sound-effect channel volume (0–1).</summary>
        public static float VolSfx
        {
            get { EnsureLoaded(); return _volSfx; }
            set { EnsureLoaded(); Set(ref _volSfx, "vol_sfx", Mathf.Clamp01(value)); }
        }

        /// <summary>Voice-over channel volume (0–1).</summary>
        public static float VolVoice
        {
            get { EnsureLoaded(); return _volVoice; }
            set { EnsureLoaded(); Set(ref _volVoice, "vol_voice", Mathf.Clamp01(value)); }
        }

        /// <summary>Fast-forward stops at the first line the player has never
        /// seen (per-title read tracking) instead of skipping blindly through
        /// new content. Off by default — skip means skip.</summary>
        public static bool SkipReadOnly
        {
            get { EnsureLoaded(); return _skipReadOnly; }
            set { EnsureLoaded(); Set(ref _skipReadOnly, "skip_read_only", value); }
        }

        /// <summary>Suppress vestibular triggers: camera shake and full-screen
        /// flashes are skipped when on.</summary>
        public static bool ReduceMotion
        {
            get { EnsureLoaded(); return _reduceMotion; }
            set { EnsureLoaded(); Set(ref _reduceMotion, "reduce_motion", value); }
        }

        /// <summary>Master sound switch. Off mutes every audio channel at the
        /// output (the per-channel volumes are preserved, so turning it back on
        /// restores them). <see cref="StageAudio"/> multiplies this into its
        /// user-volume scale and reacts to <see cref="Changed"/>.</summary>
        public static bool SoundOn
        {
            get { EnsureLoaded(); return _soundOn; }
            set { EnsureLoaded(); Set(ref _soundOn, "sound_on", value); }
        }

        /// <summary>Ступень качества арта «как в ютубе»: "2k" | "1440" | "1k";
        /// "" — игрок не выбирал, хост подбирает автодефолт по устройству.</summary>
        public static string ArtQuality
        {
            get { EnsureLoaded(); return _artQuality; }
            set { EnsureLoaded(); Set(ref _artQuality, "art_quality", value ?? ""); }
        }
        private static string _artQuality = "";

        /// <summary>Целевая частота кадров: 60 (по умолчанию) или 30 —
        /// экономия батареи; хост применяет через Application.targetFrameRate.</summary>
        public static int TargetFps
        {
            get { EnsureLoaded(); return _targetFps; }
            set { EnsureLoaded(); Set(ref _targetFps, "target_fps", value == 30 ? 30 : 60); }
        }
        private static int _targetFps = 60;

        /// <summary>ФАВОРИТ на переднем плане меню (id сущности из гардероба);
        /// пусто — героиня по умолчанию (ui.wardrobe.entity). Выбирается в
        /// гардеробе — «прикол» Ильи 26.08.</summary>
        public static string MenuFavorite
        {
            get { EnsureLoaded(); return _menuFavorite; }
            set { EnsureLoaded(); Set(ref _menuFavorite, "menu_favorite", value ?? ""); }
        }
        private static string _menuFavorite = "";

        /// <summary>Выбранный трек главного меню (id из ui.browse.music_options;
        /// пусто — базовый ui.browse.music).</summary>
        public static string MenuTrack
        {
            get { EnsureLoaded(); return _menuTrack; }
            set { EnsureLoaded(); Set(ref _menuTrack, "menu_track", value ?? ""); }
        }
        private static string _menuTrack = "";

        /// <summary>Dialogue window background opacity (0.2–1; text stays crisp —
        /// only the panel behind it fades).</summary>
        public static float DialogOpacity
        {
            get { EnsureLoaded(); return _dialogOpacity; }
            set { EnsureLoaded(); Set(ref _dialogOpacity, "dialog_opacity", Mathf.Clamp(value, LvnSettingsCatalog.BoxOpacityMin, LvnSettingsCatalog.BoxOpacityMax)); }
        }

        /// <summary>РАЗМЕР ТЕКСТА РЕПЛИК — множитель к авторскому кеглю
        /// (1 — как задумал автор). Не абсолютное число: кегль принадлежит
        /// постановке новеллы, игрок лишь подгоняет его под свои глаза и свой
        /// телефон. Просьба партнёра (TR-58): «в пункт „чтение“ просится выбор
        /// размера шрифта».
        ///
        /// <para>Границы — крайние ступени из <see cref="LvnKnobs"/>. Свой
        /// зажим здесь обещал потолок 1,4, которого ни один экран не
        /// предлагал.</para></summary>
        public static float TextScale
        {
            get { EnsureLoaded(); return _textScale; }
            set { EnsureLoaded(); Set(ref _textScale, "text_scale", LvnKnobs.ClampScale(value)); }
        }
        private static float _textScale = 1f;

        /// <summary>МАСШТАБ ИНТЕРФЕЙСА — множитель к размеру всей оболочки
        /// (1 — как нарисовано). Почему ступени не шире — сказано в
        /// <see cref="LvnKnobs.Scale"/>, оттуда же и границы.</summary>
        public static float UiScale
        {
            get { EnsureLoaded(); return _uiScale; }
            set { EnsureLoaded(); Set(ref _uiScale, "ui_scale", LvnKnobs.ClampScale(value)); }
        }
        private static float _uiScale = 1f;

        /// <summary>ТОЛЩИНА ТЕКСТА — 0 (как в гарнитуре) … 1 (жирный). Не
        /// «жирный да/нет»: у тонких гарнитур и на ярком фоне читаемость
        /// добирают именно весом, и шаг между «обычным» и «жирным» слишком
        /// груб — между ними есть промежуточное начертание.</summary>
        public static float TextWeight
        {
            get { EnsureLoaded(); return _textWeight; }
            set { EnsureLoaded(); Set(ref _textWeight, "text_weight", Mathf.Clamp01(value)); }
        }
        private static float _textWeight;

        /// <summary>ТОЛЩИНА ИНТЕРФЕЙСА — отдельная от толщины реплик. Меню
        /// читают мельком и по краю экрана, реплики — вдумчиво и по центру:
        /// одному игроку нужен жирный интерфейс на обычном тексте, другому —
        /// наоборот, и одна ручка на двоих не устраивает никого.</summary>
        public static float UiWeight
        {
            get { EnsureLoaded(); return _uiWeight; }
            set { EnsureLoaded(); Set(ref _uiWeight, "ui_weight", Mathf.Clamp01(value)); }
        }
        private static float _uiWeight;

        /// <summary>ГАРНИТУРА — ключ из каталога <c>LvnFonts.Families</c>.
        /// Пусто — та, что выбрала новелла.</summary>
        public static string FontFamily
        {
            get { EnsureLoaded(); return _fontFamily; }
            set { EnsureLoaded(); Set(ref _fontFamily, "font_family", value ?? ""); }
        }
        private static string _fontFamily = "";

        /// <summary>The reader's language code ("ru", "en", …); "" = the script's
        /// inline text (the original). The host (NovelApp) reloads the string
        /// catalog on change — new lines render in the new language at once.</summary>
        public static string Locale
        {
            get { EnsureLoaded(); return _locale; }
            set { EnsureLoaded(); Set(ref _locale, "locale", value ?? ""); }
        }
        private static string _locale;

        /// <summary>Выбирал ли игрок язык сам хоть раз — до этого хост вправе
        /// подставить язык устройства (автодефолт, как у качества арта).</summary>
        public static bool LocaleChosen => LvnKeep.Has(P + "locale");

        /// <summary>Код языка ОРИГИНАЛА (manifest.language) — пилюля оригинала
        /// зовётся именем языка («Русский»), а не словом «Оригинал».</summary>
        public static string OriginalLocale { get; set; } = "";

        /// <summary>Человеческое имя языка по коду; "" — имя оригинала.</summary>
        public static string LocaleTitle(string code)
        {
            if (string.IsNullOrEmpty(code)) code = OriginalLocale;
            switch (code)
            {
                case "ru": return "Русский";
                case "en": return "English";
                case "uk": return "Українська";
                case "de": return "Deutsch";
                case "fr": return "Français";
                case "es": return "Español";
                case "pt": return "Português";
                case "it": return "Italiano";
                case "tr": return "Türkçe";
                case "pl": return "Polski";
                case "ja": return "日本語";
                case "ko": return "한국어";
                case "zh": return "中文";
                case "": return "Оригинал";
                default: return code.ToUpperInvariant();
            }
        }

        /// <summary>Languages the running title offers, set by the host from the
        /// manifest (<c>languages</c>). The settings row shows a picker only when
        /// this is non-empty. "" (the original) is always an implicit option.</summary>
        public static System.Collections.Generic.IReadOnlyList<string> AvailableLocales
        { get; set; } = System.Array.Empty<string>();

        /// <summary>The next option in the Original → lang1 → lang2 → … cycle
        /// (pure — the settings row's tap handler).</summary>
        public static string NextLocale(string current, System.Collections.Generic.IReadOnlyList<string> available)
        {
            if (available == null || available.Count == 0) return "";
            if (string.IsNullOrEmpty(current)) return available[0];
            for (int i = 0; i < available.Count; i++)
                if (available[i] == current)
                    return i + 1 < available.Count ? available[i + 1] : "";
            return ""; // a stale pref (language removed) cycles back to the original
        }
    }
}
