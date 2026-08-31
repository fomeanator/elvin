using System;
using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>Чем настройку показывают: ползунком или переключателем.
    /// Выбор виджета — дело экрана, но ВИД величины принадлежит самой
    /// настройке: громкость всегда доля, «пропускать прочитанное» всегда да/нет.</summary>
    public enum LvnSettingKind { Range, Switch }

    /// <summary>Одна настройка: как её зовут, в каких пределах живёт и через
    /// что читается-пишется.</summary>
    public sealed class LvnSettingDef
    {
        public string Key;          // канонический ключ словаря: settings.text_speed
        public string Legacy;       // прежний ключ сцены: text_speed (может не быть)
        public string English;      // умолчание движка
        public string HintKey;      // пояснение — его показывает только оболочка
        public string HintEnglish;
        public LvnSettingKind Kind;
        public float Min, Max;
        public Func<float> Num;
        public Action<float> SetNum;
        public Func<bool> Flag;
        public Action<bool> SetFlag;
        public bool Live;           // применять на лету (громкости слышны сразу)
    }

    /// <summary>
    /// КАТАЛОГ НАСТРОЕК — что вообще можно настроить, как это зовётся и в каких
    /// пределах живёт.
    ///
    /// <para>Набор был записан ДВАЖДЫ: в меню сцены (настроить, не выходя из
    /// главы) и на экране оболочки. Пределы совпадали чудом — их сверяли
    /// руками, — а имена уже разошлись: одна и та же прозрачность окна звалась
    /// <c>settings.box_opacity</c> в оболочке и <c>window_opacity</c> в сцене,
    /// «пропускать прочитанное» — <c>settings.skip_read</c> и
    /// <c>skip_read_only</c>, эффекты — «Effects» и «Sound FX». Переводчик
    /// переводил одно из двух, и игрок видел половину настроек по-русски, а
    /// половину по-английски — в зависимости от того, откуда он их открыл.</para>
    ///
    /// <para>Здесь — ЧТО настраивается. КАК показать (компактная строка в сцене
    /// или широкая с пояснением в оболочке) остаётся экрану: вид у них разный
    /// намеренно.</para>
    ///
    /// <para>Прежний ключ сцены не выброшен, а назван: словари авторов,
    /// переведшие <c>window_opacity</c>, продолжают работать — канонический
    /// ключ спрашивается первым, прежний вторым.</para>
    /// </summary>
    public static class LvnSettingsCatalog
    {
        /// <summary>Настройки ЧТЕНИЯ: скорость, автопереход, прозрачность окна,
        /// комфорт. Порядок — от того, что видно сразу, к тонкому.</summary>
        public static List<LvnSettingDef> Reading() => new List<LvnSettingDef>
        {
            new LvnSettingDef
            {
                Key = "settings.text_speed", Legacy = "text_speed", English = "Text speed",
                HintKey = "settings.text_speed_hint", HintEnglish = "How fast lines type out",
                Kind = LvnSettingKind.Range, Min = 0.25f, Max = 3f,
                Num = () => LvnPrefs.TextSpeed, SetNum = v => LvnPrefs.TextSpeed = v,
            },
            new LvnSettingDef
            {
                Key = "settings.auto_advance", Legacy = "auto_advance", English = "Auto-advance",
                HintKey = "settings.auto_advance_hint", HintEnglish = "Lines turn by themselves",
                Kind = LvnSettingKind.Switch,
                Flag = () => LvnPrefs.AutoAdvance, SetFlag = v => LvnPrefs.AutoAdvance = v,
            },
            new LvnSettingDef
            {
                Key = "settings.auto_delay", Legacy = "auto_delay", English = "Auto delay",
                HintKey = "settings.auto_delay_hint", HintEnglish = "Pause before the next line",
                Kind = LvnSettingKind.Range, Min = 0.5f, Max = 2.5f,
                Num = () => LvnPrefs.AutoDelayScale, SetNum = v => LvnPrefs.AutoDelayScale = v,
            },
            new LvnSettingDef
            {
                Key = "settings.box_opacity", Legacy = "window_opacity", English = "Box opacity",
                HintKey = "settings.box_opacity_hint", HintEnglish = "The dialogue plate; text stays crisp",
                Kind = LvnSettingKind.Range, Min = 0.2f, Max = 1f,
                Num = () => LvnPrefs.DialogOpacity, SetNum = v => LvnPrefs.DialogOpacity = v,
            },
            new LvnSettingDef
            {
                Key = "settings.skip_read", Legacy = "skip_read_only", English = "Skip read only",
                HintKey = "settings.skip_read_hint", HintEnglish = "Fast-forward stops at new lines",
                Kind = LvnSettingKind.Switch,
                Flag = () => LvnPrefs.SkipReadOnly, SetFlag = v => LvnPrefs.SkipReadOnly = v,
            },
            new LvnSettingDef
            {
                Key = "settings.reduce_motion", Legacy = "reduce_motion", English = "Reduce motion",
                HintKey = "settings.reduce_motion_hint", HintEnglish = "No camera shake or flashes",
                Kind = LvnSettingKind.Switch,
                Flag = () => LvnPrefs.ReduceMotion, SetFlag = v => LvnPrefs.ReduceMotion = v,
            },
        };

        /// <summary>
        /// Громкости. ДВА РЕЖИМА, и это решение новеллы (<c>ui.settings.
        /// simple_audio</c>): в простом «Звук» ведёт эффекты, эмбиент и голос
        /// одним движком — игроку незачем знать разницу, если игра ею не
        /// пользуется.
        /// </summary>
        public static List<LvnSettingDef> Audio(bool simple)
        {
            var music = new LvnSettingDef
            {
                Key = "settings.music", Legacy = "music", English = "Music",
                HintKey = "settings.music_hint", HintEnglish = "Story and menu tracks",
                Kind = LvnSettingKind.Range, Min = 0f, Max = 1f, Live = true,
                Num = () => LvnPrefs.VolMusic, SetNum = v => LvnPrefs.VolMusic = v,
            };
            if (simple)
                return new List<LvnSettingDef>
                {
                    music,
                    new LvnSettingDef
                    {
                        Key = "settings.sounds", Legacy = "sound", English = "Sounds",
                        HintKey = "settings.sounds_hint", HintEnglish = "Choices, scene effects and ambience",
                        Kind = LvnSettingKind.Range, Min = 0f, Max = 1f, Live = true,
                        Num = () => LvnPrefs.VolSfx,
                        SetNum = v => { LvnPrefs.VolSfx = v; LvnPrefs.VolAmbient = v; LvnPrefs.VolVoice = v; },
                    },
                };
            return new List<LvnSettingDef>
            {
                music,
                new LvnSettingDef
                {
                    Key = "settings.ambient", Legacy = "ambient", English = "Ambience",
                    Kind = LvnSettingKind.Range, Min = 0f, Max = 1f, Live = true,
                    Num = () => LvnPrefs.VolAmbient, SetNum = v => LvnPrefs.VolAmbient = v,
                },
                new LvnSettingDef
                {
                    Key = "settings.sfx", Legacy = "sfx", English = "Effects",
                    Kind = LvnSettingKind.Range, Min = 0f, Max = 1f, Live = true,
                    Num = () => LvnPrefs.VolSfx, SetNum = v => LvnPrefs.VolSfx = v,
                },
                new LvnSettingDef
                {
                    Key = "settings.voice", Legacy = "voice", English = "Voice",
                    Kind = LvnSettingKind.Range, Min = 0f, Max = 1f, Live = true,
                    Num = () => LvnPrefs.VolVoice, SetNum = v => LvnPrefs.VolVoice = v,
                },
            };
        }

        /// <summary>Подпись настройки: канонический ключ, затем прежний ключ
        /// сцены, затем английское умолчание. Тема (если её дали) отвечает
        /// первой — у неё есть подписи меню, положенные автором.</summary>
        public static string Label(LvnSettingDef d, VnTheme theme = null)
        {
            if (d == null) return "";
            if (theme != null)
            {
                var byTheme = theme.Word(d.Key, null);
                if (!string.IsNullOrEmpty(byTheme)) return byTheme;
                if (!string.IsNullOrEmpty(d.Legacy))
                {
                    var legacy = theme.Word(d.Legacy, null);
                    if (!string.IsNullOrEmpty(legacy)) return legacy;
                }
                return d.English;
            }
            var word = Lvn.Content.LvnWords.Of(d.Key, null);
            if (!string.IsNullOrEmpty(word)) return word;
            if (!string.IsNullOrEmpty(d.Legacy))
            {
                word = Lvn.Content.LvnWords.Of(d.Legacy, null);
                if (!string.IsNullOrEmpty(word)) return word;
            }
            return d.English;
        }

        /// <summary>Пояснение под подписью; пустое, когда его нет.</summary>
        public static string Hint(LvnSettingDef d)
            => d == null || string.IsNullOrEmpty(d.HintKey)
                ? null
                : Lvn.Content.LvnWords.Of(d.HintKey, d.HintEnglish);
    }
}
