#!/usr/bin/env bash
# ЧЕГО У НАС НЕТ ДЛЯ ТЕХ, КОМУ ИГРАТЬ ТРУДНЕЕ.
#
# Это не страж (ломать нечего) и не проверка обещания — это ЗАМЕР состояния,
# который можно повторить через месяц и увидеть движение. Заведён потому, что
# сравнение с цехом показало единственное место, где мы отстаём не на детали, а
# принципиально: у Ren'Py самоvoice работает из коробки (игрок жмёт «v», текст
# читает синтезатор операционной системы), и для части игроков это не удобство,
# а условие играбельности вообще.
#
# Считаются ФАКТЫ по коду, а не намерения:
#
#   ОЗВУЧКА     обращается ли движок к синтезу речи или к дереву доступности
#               платформы (Android TalkBack / iOS VoiceOver);
#   ЧИТАЕМОСТЬ  ручки размера: интерфейс, реплики, выбор fonts;
#   КОНТРАСТ    есть ли тема повышенной contrastности;
#   ДИСЛЕКСИЯ   есть ли гарнитура для дислексии.
#
#   qa/accessibility-audit.sh
#
# Стенд НЕ красит прогон: он печатает картину и выходит с нулём. Красить нечего
# — отсутствие фичи не поломка, но и забывать о нём не следует.
set -uo pipefail
cd "$(dirname "$0")/.."

RT="unity/Packages/com.lvn.engine/Runtime"
SH_RT="unity/Packages/com.lvn.engine.shell/Runtime"

count() { grep -rl "$1" $RT $SH_RT 2>/dev/null | grep -v Tests | wc -l | tr -d ' '; }

voice=$(( $(count "UnityEngine.Accessibility") + $(count "SpeechSynth") + $(count "TextToSpeech") ))
ui_knob=$(count "ui_scale")
text_knob=$(count "text_scale\|dialogue_scale\|DialogueTextScale")
fonts=$(grep -rn "font_family\|FontFamily" $RT 2>/dev/null | grep -v Tests | wc -l | tr -d ' ')
contrast=$(count "high_contrast\|HighContrast")
dyslexia=$(count "dyslexi\|OpenDyslexic")

echo "  самоозвучка / дерево доступности: $voice мест в коде"
echo "  масштаб интерфейса:               $ui_knob"
echo "  размер реплик:                    $text_knob"
echo "  выбор гарнитуры:                  $fonts упоминани(й)"
echo "  тема высокой контрастности:       $contrast"
echo "  гарнитура для дислексии:          $dyslexia"
echo

have=0
[ "$voice" -gt 0 ] && have=$((have+1))
[ "$ui_knob" -gt 0 ] && have=$((have+1))
[ "$text_knob" -gt 0 ] && have=$((have+1))
[ "$contrast" -gt 0 ] && have=$((have+1))
[ "$dyslexia" -gt 0 ] && have=$((have+1))
echo "итого: $have из 5 опор доступности"

if [ "$voice" = "0" ]; then
  echo
  echo "ГЛАВНОЕ, ЧЕГО НЕТ: самоозвучки. У Ren'Py она встроена и не требует от"
  echo "автора игры ничего; у нас ноль обращений к синтезу речи и к дереву"
  echo "доступности платформы. Дорога при этом открыта: UnityEngine."
  echo "AccessibilityModule входит в наши сборки — проверено по журналу сборки APK."
fi
exit 0
