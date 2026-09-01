#!/bin/zsh
# СОБРАТЬ C#, НЕ ЗАНИМАЯ РЕДАКТОР.
#
# qa/run-all.sh требует лицензию Unity, а она одна: пока открыт редактор,
# batchmode не стартует и возвращает «нет результатов» — не ошибку, а тишину,
# которую легко принять за успех. Правки при этом копятся непроверенными.
#
# Здесь собираются те же пять сборок тем же Roslyn'ом, каким собирает сама
# Unity, но против её управляемых DLL напрямую. Тесты НЕ гоняются — это не
# замена прогону. Зато ловится то, ради чего прогона ждут чаще всего:
# опечатка, разъехавшаяся подпись, потерянный using, тип, которого нет.
#
# ГРАБЛЯ, СТОИВШАЯ ЧАСА: Roslyn из MonoBleedingEdge — версии 3.7, и целевую
# типизацию условного выражения (C# 9) он не знает. Он «находил» ошибку там,
# где редактор собирает молча. Берём тот же компилятор, что и Unity:
# DotNetSdkRoslyn через её же netcore.
set -e
UNITY_VERSION=${UNITY_VERSION:-6000.4.5f1}
U=/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents
DOTNET=$U/Resources/Scripting/NetCoreRuntime/dotnet
CSC=$U/Resources/Scripting/DotNetSdkRoslyn/csc.dll
REF=$U/Resources/Scripting/Managed/UnityEngine
NS=$U/Resources/Scripting/NetStandard/ref/2.1.0
REPO=${0:A:h:h}
LIB=$REPO/unity/TestHost/Library
OUT=${1:-$REPO/qa/reports/compile}
mkdir -p $OUT

[[ -x $DOTNET ]] || { echo "нет Unity $UNITY_VERSION — задайте UNITY_VERSION"; exit 2 }

refs=(-nostdlib -noconfig)
for d in $NS/*.dll; do refs+=(-r:$d); done
for d in $REF/*.dll; do refs+=(-r:$d); done
refs+=(-r:$LIB/ScriptAssemblies/UnityEngine.UI.dll)
refs+=(-r:$(ls $LIB/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll | head -1))
for d in $LIB/PackageCache/com.unity.cloud.ktx@*/Runtime/Plugins/*.dll(N); do refs+=(-r:$d); done

common=(-nologo -target:library -langversion:9.0 -unsafe \
        -nowarn:0169,0414,0649,0067,1701,1702,CS8632 \
        -define:UNITY_2021_1_OR_NEWER -define:UNITY_EDITOR -define:UNITY_STANDALONE_OSX)

build () {  # имя  выход-имя  ссылки…  ‹--› файлы
  local name=$1; shift
  local files=(${(f)"$(find $@ -name '*.cs')"})
  print "== $name (${#files} файлов)"
  $DOTNET $CSC $common $refs $extra -out:$OUT/$name.dll $files
}

extra=()
build Lvn.Engine $REPO/unity/Packages/com.lvn.engine/Runtime -not -path '*/UI/*' -not -path '*/Content/*'
extra=(-r:$OUT/Lvn.Engine.dll)
build Lvn.Engine.Content $REPO/unity/Packages/com.lvn.engine/Runtime/Content
extra+=(-r:$OUT/Lvn.Engine.Content.dll)
build Lvn.Engine.UI $REPO/unity/Packages/com.lvn.engine/Runtime/UI
extra+=(-r:$OUT/Lvn.Engine.UI.dll)
build Lvn.Engine.Services $REPO/unity/Packages/com.lvn.engine.services/Runtime
extra+=(-r:$OUT/Lvn.Engine.Services.dll)
build Lvn.Engine.Shell $REPO/unity/Packages/com.lvn.engine.shell/Runtime
print "СОБРАЛОСЬ ВСЁ. Это НЕ прогон: тесты не гонялись."
