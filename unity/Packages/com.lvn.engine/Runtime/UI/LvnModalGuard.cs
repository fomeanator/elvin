namespace Lvn.UI
{
    /// <summary>
    /// СЧЁТЧИК МОДАЛЕЙ ОБОЛОЧКИ — мостик «шелл → сцена» для системной кнопки
    /// «назад». Сцена (VnStage) и оболочка живут в разных пакетах и оба слышат
    /// Escape; без одной правды оба реагировали разом: магазин, открытый из
    /// сюжетного гардероба, закрывался ВМЕСТЕ с гардеробом под ним. Правило:
    /// пока открыта хоть одна модаль оболочки, «назад» принадлежит ей — сцена
    /// молчит. Пишет сюда только роутер оболочки.
    /// </summary>
    public static class LvnModalGuard
    {
        /// <summary>Сколько модальных экранов оболочки открыто. Теперь это
        /// ОКНО в Режиссёра, а не своя правда: «кто сейчас на экране» знает он
        /// один, а этот класс остаётся ради хостов, которые уже на него
        /// смотрят.</summary>
        public static int Depth
        {
            get => _depth;
            set
            {
                _depth = value < 0 ? 0 : value;
                if (_depth > 0) LvnScreenDirector.Current.Open(LvnScreenDirector.ShellModal);
                else LvnScreenDirector.Current.Close(LvnScreenDirector.ShellModal);
            }
        }
        private static int _depth;

        public static bool AnyOpen => LvnScreenDirector.Current.IsOpen(LvnScreenDirector.ShellModal);
    }
}
