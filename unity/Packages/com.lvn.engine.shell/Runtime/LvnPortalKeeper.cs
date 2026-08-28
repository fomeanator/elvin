using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// СТВОРНИК — насколько створ открыт прямо сейчас и кто за это отвечает.
    ///
    /// <para>Створ — единственная часть кадра, живущая МИМО партитуры слоёв
    /// (<see cref="LvnStageScore"/>): кадровая модель его не знает, поэтому при
    /// смене слоя он не проступает и не гаснет сам. Это не упущение, а свойство
    /// перехода — створ обязан пережить смену сцены на меню, иначе шов между
    /// главой и витриной станет виден. Но раз он переживает всё, кто-то должен
    /// помнить, открыт ли он.</para>
    ///
    /// <para>Помнили девять мест хореографии: каждое собирало команду руками
    /// (<c>op portal</c> с координатами, радиусом, цветом и спрайтом), само
    /// решало, каким числом обозначить «открыт», и само не забывало закрыть.
    /// Рядом жил таймер схлопывания с поколением показа — ручная защита от
    /// того, что створ останется висеть после ухода в главу.</para>
    ///
    /// <para>Ответственность: собрать команду створа по авторской настройке,
    /// держать поколение показа и гасить створ, когда его никто не держит.
    /// ХОРЕОГРАФИЯ здесь не живёт: сколько ждать между шагами, когда героиня
    /// входит и в какой момент кадр гаснет — дело того, кто ведёт сцену.</para>
    /// </summary>
    public sealed class LvnPortalKeeper
    {
        private readonly ILvnStage _stage;
        private readonly System.Func<PortalConfig> _config;

        /// <summary>Сколько створ живёт на главной, прежде чем схлопнуться в
        /// точку (решение Ильи 28.08: врата — событие, а не деталь фона; вечно
        /// висеть за героиней им незачем).</summary>
        public const float MenuLifetime = 4f;

        /// <summary>Поколение показа: уход в главу или новый показ отменяет
        /// прежнее схлопывание — иначе таймер прошлого захода закрыл бы створ
        /// посреди перехода.</summary>
        private int _generation;

        public LvnPortalKeeper(ILvnStage stage, System.Func<PortalConfig> config)
        {
            _stage = stage;
            _config = config;
        }

        private PortalConfig Cfg => _config?.Invoke();

        /// <summary>Есть ли створ у этой новеллы вообще.</summary>
        public bool Available => _stage != null && Cfg != null;

        /// <summary>ОТКРЫТЬ НА СТОЛЬКО-ТО за столько-то секунд. 0 — закрыть.</summary>
        public void Set(float open, float seconds)
        {
            var p = Cfg;
            if (_stage == null || p == null) return;
            _stage.ApplyStage(Command(p, open, seconds), LvnSender.Cutscene);
        }

        /// <summary>ПОКАЗАТЬ НА ГЛАВНОЙ — тускло, вполсилы: это часть мира, а не
        /// всплывающий эффект. Через <see cref="MenuLifetime"/> схлопнется сам,
        /// если к тому времени его никто не перехватит.</summary>
        public void ShowOnMenu()
        {
            var p = Cfg;
            if (_stage == null || p == null) return;
            Set(p.idle ?? 0.34f, 0.6f);
            LvnAsync.Fire(CollapseLaterAsync(++_generation), "MenuPortalIdle");
        }

        /// <summary>ПЕРЕХВАТИТЬ — сцена уходит в главу, и таймер главной больше
        /// не властен над створом.</summary>
        public void Hold() => _generation++;

        private async Task CollapseLaterAsync(int generation)
        {
            await Task.Delay(LvnMotion.Ms((int)(MenuLifetime * 1000f)));
            if (_stage == null || Cfg == null) return;
            if (generation != _generation) return;   // показ уже сменился
            LvnLog.Trace($"[lvn-portal] створ на главной отжил {MenuLifetime:0}с — схлопываем в точку");
            Set(0f, 2.5f);
        }

        /// <summary>
        /// КОМАНДА СТВОРА по авторской настройке.
        ///
        /// <para>Центр кадра и вдвое шире (Илья 28.08): створ сбоку читался как
        /// «дырка в углу», из центра — как то, во что входят. Радиус — доля
        /// МЕНЬШЕЙ стороны, 0.6 даёт круг чуть шире экрана телефона по
        /// вертикали.</para>
        /// </summary>
        private static JObject Command(PortalConfig p, float open, float dur)
        {
            var cmd = new JObject
            {
                ["op"] = "portal",
                ["open"] = open,
                ["x"] = p.x ?? 0.5f,
                ["y"] = p.y ?? 0.5f,
                ["radius"] = p.radius ?? 0.60f,
                ["dur"] = dur,
            };
            if (!string.IsNullOrEmpty(p.color)) cmd["color"] = p.color;
            // Ядро створа картинкой: процедурный вихрь читается «ломаными
            // линиями», а готовый шар энергии — тем, чем он и должен быть.
            if (!string.IsNullOrEmpty(p.sprite)) cmd["sprite_url"] = p.sprite;
            return cmd;
        }
    }
}
