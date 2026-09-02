#!/bin/zsh
# СОБРАТЬ C#, НЕ ЗАНИМАЯ РЕДАКТОР.
#
# qa/run-all.sh требует лицензию Unity, а она одна: пока открыт редактор,
# batchmode не стартует и возвращает «нет результатов» — не ошибку, а тишину,
# которую легко принять за успех. Правки при этом копятся непроверенными: 01.09
# так накопилось три подряд.
#
# Тесты здесь НЕ гоняются. Это не замена прогону — это способ поймать то, ради
# чего прогона ждут чаще всего: опечатку, разъехавшуюся подпись, потерянный
# using, недоделанного тестового двойника.
#
# КАК ЭТО РАБОТАЕТ. Unity оставляет свои собственные командные строки
# компилятора в Library/Bee/artifacts/*.dag/<сборка>.rsp — со всеми ссылками,
# всеми -define и нужными фасадами. Мы берём ИХ, меняем только список
# исходников на сегодняшний и выход. Это единственный способ не переизобретать
# набор ссылок: попытка собрать его руками упирается то в netstandard против
# mscorlib (nunit собран под 4.8), то в UnityEditor.dll против его же
# CoreModule — по полчаса на каждую.
#
# Следствие: rsp должен существовать, то есть редактор хотя бы раз собирал
# проект. Если сборки в списке нет — её пропускаем и говорим об этом вслух.
set -e
REPO=${0:A:h:h}
UNITY_VERSION=${UNITY_VERSION:-6000.4.5f1}
U=/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents
DOTNET=$U/Resources/Scripting/NetCoreRuntime/dotnet
CSC=$U/Resources/Scripting/DotNetSdkRoslyn/csc.dll
HOST=$REPO/unity/TestHost
OUT=$REPO/qa/reports/compile
mkdir -p $OUT

[[ -x $DOTNET ]] || { print "нет Unity $UNITY_VERSION — задайте UNITY_VERSION"; exit 2 }

DAG=(${(f)"$(ls -d $HOST/Library/Bee/artifacts/*.dag 2>/dev/null)"})
[[ ${#DAG} -gt 0 ]] || { print "нет Library/Bee/artifacts/*.dag — редактор ни разу не собирал проект"; exit 2 }
DAG=${DAG[1]}

# Какие исходники принадлежат сборке: те же корни, что у её asmdef.
typeset -A ROOTS
ROOTS[Lvn.Engine]=$REPO/unity/Packages/com.lvn.engine/Runtime
ROOTS[Lvn.Engine.Content]=$REPO/unity/Packages/com.lvn.engine/Runtime/Content
ROOTS[Lvn.Engine.UI]=$REPO/unity/Packages/com.lvn.engine/Runtime/UI
ROOTS[Lvn.Engine.Services]=$REPO/unity/Packages/com.lvn.engine.services/Runtime
ROOTS[Lvn.Engine.Shell]=$REPO/unity/Packages/com.lvn.engine.shell/Runtime
ROOTS[Lvn.Engine.Editor]=$REPO/unity/Packages/com.lvn.engine/Editor
ROOTS[Lvn.Engine.Tests]=$REPO/unity/Packages/com.lvn.engine/Tests/Editor
ROOTS[Lvn.Engine.Tests.Runtime]=$REPO/unity/Packages/com.lvn.engine/Tests/Runtime
# Тесты оболочки. До 02.09 своей сборки у них не было: они падали в
# Assembly-CSharp-Editor, которую этот гейт не собирает вовсе, — и ошибка
# компиляции в них находилась только полным прогоном Unity, через 13 минут.
ROOTS[Lvn.Engine.Shell.Tests]=$REPO/unity/Packages/com.lvn.engine.shell/Tests/Editor

ORDER=(Lvn.Engine Lvn.Engine.Content Lvn.Engine.UI Lvn.Engine.Services
       Lvn.Engine.Shell Lvn.Engine.Editor Lvn.Engine.Tests Lvn.Engine.Tests.Runtime
       Lvn.Engine.Shell.Tests)

fail=0
typeset -a BUILT
for name in $ORDER; do
  rsp=$DAG/$name.rsp
  [[ -f $rsp ]] || { print "== $name — пропуск: нет $name.rsp"; continue }
  root=$ROOTS[$name]
  # Вложенные сборки со своим asmdef исключаем из родителя.
  local -a files
  if [[ $name == Lvn.Engine ]]; then
    files=(${(f)"$(find $root -name '*.cs' -not -path '*/UI/*' -not -path '*/Content/*')"})
  else
    files=(${(f)"$(find $root -name '*.cs')"})
  fi
  # Из rsp берём ВСЁ, кроме списка исходников и путей вывода.
  #
  # И ПЕРЕНАПРАВЛЯЕМ ССЫЛКИ НА СВОИ СБОРКИ. Без этого оболочка собиралась бы
  # против ТОЙ Lvn.Engine.UI.dll, которую редактор сложил в прошлый раз, —
  # и новый дом в UI, которым уже пользуется экран, «не существовал бы».
  # Проверка, слепая к изменениям через границу сборки, отвечает не на тот
  # вопрос: она проверяет вчерашний код с сегодняшними вызовами.
  # ЯВНЫЙ СБРОС. `local` вне функции ничего не ограничивает: массив живёт
  # между витками цикла, и `fixed+=` копил ссылки ВСЕХ прошлых сборок. Первой
  # это сходило с рук, а редакторная получила 120 чужих фасадов и легла на
  # «две сборки с одинаковой личностью». Ошибка выглядела как чужая — про
  # дубликаты в наборе Unity, — хотя дубликаты сделал я сам.
  local -a opts fixed
  opts=(${(f)"$(grep -v '^\"' $rsp | grep -v '^-out:' | grep -v '^-refout:')"})
  fixed=()
  for o in $opts; do
    local swapped=$o
    for done_name in $BUILT; do
      swapped=${swapped//Library\/Bee\/artifacts\/*.dag\/$done_name.ref.dll/$OUT/$done_name.dll}
      swapped=${swapped//Library\/Bee\/artifacts\/*.dag\/$done_name.dll/$OUT/$done_name.dll}
    done
    fixed+=($swapped)
  done
  opts=($fixed)
  print "== $name (${#files} файлов)"
  ( cd $HOST && $DOTNET $CSC -nologo $opts -out:$OUT/$name.dll $files ) || fail=1
  BUILT+=($name)
done
[[ $fail == 0 ]] || { print "СБОРКА УПАЛА"; exit 1 }

# СТРАЖИ ТЕКСТА — ЗДЕСЬ ЖЕ, А НЕ ЧЕРЕЗ ДВЕ МИНУТЫ.
#
# Компилятор ловит «не собирается». Есть второй класс поломок, которые он
# пропускает, а полный прогон находит только в го-фазе, то есть минуты спустя:
# докблок, оторванный от предмета; почти-двойник; дом, не доехавший до карты.
#
# 01.09 я четырежды за день оторвал объяснение от члена, вставляя новый способ
# ПЕРЕД докблоком соседа, и каждый раз узнавал об этом из красного прогона.
# Правило «ставь за концом соседа» записано трижды и трижды нарушено. Правило,
# которое приходится помнить, стоит дороже проверки, которая стоит пять секунд.
#
# Гоняются только БЫСТРЫЕ и только те, что читают исходники: пять секунд на всё.
# Остальное — дело прогона.
if command -v go >/dev/null 2>&1; then
  print "== стражи текста"
  ( cd $REPO/tools/lvnconv/lvn && go test -count=1 \
      -run "TestNoExplanationLostItsSubject|TestNoNearTwins|TestEveryLivedInHomeIsOnTheMap|TestNoHandHeldPairs|TestDawnScreensTakeColorFromOneHome" . ) \
    || { print "СТРАЖИ ТЕКСТА КРАСНЫЕ (сборка при этом цела)"; exit 1 }
fi

print "СОБРАЛОСЬ ВСЁ. Это НЕ прогон: тесты Unity не гонялись."
