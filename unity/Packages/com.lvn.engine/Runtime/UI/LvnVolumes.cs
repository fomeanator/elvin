namespace Lvn.UI
{
    /// <summary>
    /// ЗВУКОРЕЖИССЁР — какая громкость у канала прямо сейчас.
    ///
    /// <para>Правило простое: громкость канала — это ползунок этого канала,
    /// помноженный на общий тумблер «звук». Записано оно было ЧЕТЫРЕЖДЫ и
    /// каждый раз иначе: сцена держала множитель <c>Master</c> и таблицу
    /// каналов (music/ambient/sfx), музыка меню считала <c>SoundOn ? VolMusic
    /// : 0</c> дважды в двух методах, а озвучка бралась ПРЯМО с ползунка.</para>
    ///
    /// <para>Последнее и было дефектом: реплика начинала звучать с полной
    /// громкостью при выключенном тумблере — до ближайшего пересчёта громкостей
    /// её было слышно. Канал озвучки в таблице каналов сцены просто забыли.</para>
    ///
    /// <para>Ответственность: одна формула на все каналы. Кто когда играет —
    /// дело сцены и оболочки; сколько это стоит в громкости — дело этой
    /// роли.</para>
    /// </summary>
    public static class LvnVolumes
    {
        /// <summary>Имена каналов — те же, что в авторских командах звука.</summary>
        public const string Music = "music";
        public const string Ambient = "ambient";
        public const string Sfx = "sfx";
        public const string Voice = "voice";
        public const string Ui = "ui";

        /// <summary>Общий тумблер: выключенный складывает ВСЕ каналы в тишину.
        /// Отдельный множитель, а не «ползунки в ноль» — тумблер не должен
        /// стирать положение ползунков, к которым игрок вернётся.</summary>
        public static float Master => LvnPrefs.SoundOn ? 1f : 0f;

        /// <summary>Громкость канала: его ползунок под общим тумблером.
        /// Неизвестный канал считается звуковым эффектом — новая команда звука
        /// не должна звучать мимо настроек только потому, что её забыли
        /// добавить в таблицу.</summary>
        public static float Of(string channel)
        {
            float own = channel == Music ? LvnPrefs.VolMusic
                      : channel == Ambient ? LvnPrefs.VolAmbient
                      : channel == Voice ? LvnPrefs.VolVoice
                      : LvnPrefs.VolSfx;
            return Master * own;
        }
    }
}
