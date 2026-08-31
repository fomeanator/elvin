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
    /// <para>Прежние имена ключей знает СЛОВАРЬ (<see cref="Lvn.Content.LvnWordAliases"/>):
    /// словари авторов, переведшие <c>window_opacity</c>, продолжают работать,
    /// и не только здесь — то же правило чинит «Закрыть», «Галерея»,
    /// «История» и «Отмена» на всех экранах оболочки.</para>
    /// </summary>
    public static class LvnSettingsCatalog
    {
        // ПРЕДЕЛЫ — СВОЙСТВО САМОЙ НАСТРОЙКИ, а не ползунка и не хранилища.
        //
        // Числа стояли в двух домах: здесь их знал ползунок, а рядом, в
        // LvnPrefs, тот же диапазон зажимал записываемое значение. Сегодня они
        // совпадают, потому что их сверили руками; разойдись они — и ползунок
        // поехал бы туда, откуда хранилище его молча возвращает, а игрок
        // увидел бы, как ручка «пружинит» назад без объяснения.
        public const float TextSpeedMin = 0.25f, TextSpeedMax = 3f;
        public const float AutoDelayMin = 0.5f, AutoDelayMax = 2.5f;
        public const float BoxOpacityMin = 0.2f, BoxOpacityMax = 1f;
        public const float VolumeMin = 0f, VolumeMax = 1f;

        /// <summary>Настройки ЧТЕНИЯ: скорость, автопереход, прозрачность окна,
        /// комфорт. Порядок — от того, что видно сразу, к тонкому.</summary>
        public static List<LvnSettingDef> Reading() => new List<LvnSettingDef>
        {
            new LvnSettingDef
            {
                Key = "settings.text_speed", English = "Text speed",
                HintKey = "settings.text_speed_hint", HintEnglish = "How fast lines type out",
                Kind = LvnSettingKind.Range, Min = TextSpeedMin, Max = TextSpeedMax,
                Num = () => LvnPrefs.TextSpeed, SetNum = v => LvnPrefs.TextSpeed = v,
            },
            new LvnSettingDef
            {
                Key = "settings.auto_advance", English = "Auto-advance",
                HintKey = "settings.auto_advance_hint", HintEnglish = "Lines turn by themselves",
                Kind = LvnSettingKind.Switch,
                Flag = () => LvnPrefs.AutoAdvance, SetFlag = v => LvnPrefs.AutoAdvance = v,
            },
            new LvnSettingDef
            {
                Key = "settings.auto_delay", English = "Auto delay",
                HintKey = "settings.auto_delay_hint", HintEnglish = "Pause before the next line",
                Kind = LvnSettingKind.Range, Min = AutoDelayMin, Max = AutoDelayMax,
                Num = () => LvnPrefs.AutoDelayScale, SetNum = v => LvnPrefs.AutoDelayScale = v,
            },
            new LvnSettingDef
            {
                Key = "settings.box_opacity", English = "Box opacity",
                HintKey = "settings.box_opacity_hint", HintEnglish = "The dialogue plate; text stays crisp",
                Kind = LvnSettingKind.Range, Min = BoxOpacityMin, Max = BoxOpacityMax,
                Num = () => LvnPrefs.DialogOpacity, SetNum = v => LvnPrefs.DialogOpacity = v,
            },
            new LvnSettingDef
            {
                Key = "settings.skip_read", English = "Skip read only",
                HintKey = "settings.skip_read_hint", HintEnglish = "Fast-forward stops at new lines",
                Kind = LvnSettingKind.Switch,
                Flag = () => LvnPrefs.SkipReadOnly, SetFlag = v => LvnPrefs.SkipReadOnly = v,
            },
            new LvnSettingDef
            {
                Key = "settings.reduce_motion", English = "Reduce motion",
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
                Key = "settings.music", English = "Music",
                HintKey = "settings.music_hint", HintEnglish = "Story and menu tracks",
                Kind = LvnSettingKind.Range, Min = VolumeMin, Max = VolumeMax, Live = true,
                Num = () => LvnPrefs.VolMusic, SetNum = v => LvnPrefs.VolMusic = v,
            };
            if (simple)
                return new List<LvnSettingDef>
                {
                    music,
                    new LvnSettingDef
                    {
                        Key = "settings.sounds", English = "Sounds",
                        HintKey = "settings.sounds_hint", HintEnglish = "Choices, scene effects and ambience",
                        Kind = LvnSettingKind.Range, Min = VolumeMin, Max = VolumeMax, Live = true,
                        Num = () => LvnPrefs.VolSfx,
                        SetNum = v => { LvnPrefs.VolSfx = v; LvnPrefs.VolAmbient = v; LvnPrefs.VolVoice = v; },
                    },
                };
            return new List<LvnSettingDef>
            {
                music,
                new LvnSettingDef
                {
                    Key = "settings.ambient", English = "Ambience",
                    Kind = LvnSettingKind.Range, Min = VolumeMin, Max = VolumeMax, Live = true,
                    Num = () => LvnPrefs.VolAmbient, SetNum = v => LvnPrefs.VolAmbient = v,
                },
                new LvnSettingDef
                {
                    Key = "settings.sfx", English = "Effects",
                    Kind = LvnSettingKind.Range, Min = VolumeMin, Max = VolumeMax, Live = true,
                    Num = () => LvnPrefs.VolSfx, SetNum = v => LvnPrefs.VolSfx = v,
                },
                new LvnSettingDef
                {
                    Key = "settings.voice", English = "Voice",
                    Kind = LvnSettingKind.Range, Min = VolumeMin, Max = VolumeMax, Live = true,
                    Num = () => LvnPrefs.VolVoice, SetNum = v => LvnPrefs.VolVoice = v,
                },
            };
        }

        /// <summary>Подпись настройки. Прежнее имя ключа подставит сам словарь
        /// (<see cref="Lvn.Content.LvnWordAliases"/>); тема, если её дали,
        /// отвечает первой — у неё есть подписи меню, положенные автором.</summary>
        public static string Label(LvnSettingDef d, VnTheme theme = null)
        {
            if (d == null) return "";
            return theme != null ? theme.Word(d.Key, d.English) : Lvn.Content.LvnWords.Of(d.Key, d.English);
        }

        /// <summary>Пояснение под подписью; пустое, когда его нет.</summary>
        public static string Hint(LvnSettingDef d)
            => d == null || string.IsNullOrEmpty(d.HintKey)
                ? null
                : Lvn.Content.LvnWords.Of(d.HintKey, d.HintEnglish);
    }
}
