#!/bin/bash
# ПРОГРЕТЬ КОДЫ ЗАРАНЕЕ — пакетом, а не по одному запросу игрока.
#
# Сервер кодирует .ktx2 ЛЕНИВО и на первый запрос отвечает 404: UASTC съедает
# все ядра, и десяток холодных запросов при входе в главу застопорил бы сцену.
# Расчёт был на «первый заход платит, остальные получают сжатое» — но пока
# клиент трактовал 404 как «нет никогда», второго захода не наступало, и
# быстрый формат не использовался почти никогда.
#
# Этот проход собирает коды заранее, на той же машине и ТЕМИ ЖЕ флагами, что
# сервер (иначе разъедутся ориентация и качество). Гоняется руками перед
# выкладкой или ночью — не в горячем пути.
#
#   tools/warm-ktx2.sh ~/ominis/tr-content
set -u
ROOT="${1:?укажите каталог контента}"
BASISU="$(command -v basisu)" || { echo "basisu не найден: brew install basis_universal"; exit 1; }

made=0; had=0; failed=0
while IFS= read -r src; do
  out="${src%.*}.ktx2"
  case "$src" in
    */pixel/*) continue ;;          # пиксель-арт: блочное сжатие размажет сетку
    *@mini.*)  continue ;;          # крошка-заготовка живёт растром намеренно
    */ui/*)
      # КРУПНОЕ В /ui/ — НЕ ОБШИВКА. Папку исключали целиком, и для кнопок с
      # рамками это верно: тонкие линии блочное сжатие портит. Но там же по
      # МЕСТУ лежит полотно витрины 2000×1500 — и оно оставалось без кода
      # нигде: скрипт его пропускал, а на прод-боксе кодировать может быть
      # нечем. Игрок платил за это тремя секундами на первом экране.
      # Порог тот же, что у сервера (ktx2ChromeBox = 1024).
      w=$(sips -g pixelWidth  "$src" 2>/dev/null | awk '/pixelWidth/{print $2}')
      h=$(sips -g pixelHeight "$src" 2>/dev/null | awk '/pixelHeight/{print $2}')
      [ -n "${w:-}" ] && [ -n "${h:-}" ] || { continue; }
      [ "$w" -ge 1024 ] || [ "$h" -ge 1024 ] || continue
      ;;
  esac
  if [ -f "$out" ]; then had=$((had+1)); continue; fi
  if nice -n 19 "$BASISU" -ktx2 -uastc -uastc_level 2 -uastc_rdo_l 1.0 -y_flip -mipmap \
        "$src" -output_file "$out" >/dev/null 2>&1; then
    made=$((made+1)); printf '.'
  else
    failed=$((failed+1)); printf 'x'
  fi
done < <(find "$ROOT" \( -path '*/bg/*' -o -path '*/art/*' -o -path '*/sprites/*' \
                        -o -path '*/spine/*' -o -path '*/ui/*' \) \
              \( -name '*.png' -o -name '*.jpg' \) )

echo
echo "коды: собрано $made, уже были $had, не вышло $failed"
