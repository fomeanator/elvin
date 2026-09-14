#!/usr/bin/env bash
# qa/release.sh — ВЫПУСК APK ИЗ ЗАФИКСИРОВАННОГО КОММИТА: dev или prod.
#
#   SSHPASS='…' qa/release.sh dev     # origin/dev  → <префикс>-dev-<штамп>.apk, пакет с суффиксом .dev, …/<dev-latest>
#   SSHPASS='…' qa/release.sh prod    # origin/main → <префикс>-<штамп>.apk,     пакет как есть,           …/<prod-latest>
#
# Зачем: сборка из рабочего дерева не отвечает на вопрос «что в APK» — в
# дереве лежит незакоммиченное нескольких людей, и «где мои правки» потом не
# разобрать. Здесь APK собирается ТОЛЬКО из коммита на origin, в отдельном
# дереве (~/ominis/builds/release-<канал>), после трёх проверок (компиляция,
# Go-стражи, стенд утечек), а хэш коммита и канал зашиваются в сборку
# (Настройки → Версия, строка [lvn-build] в логе устройства).
#
# Проект сборки ОДИН (клонов не бывает): на время сборки его пакеты указывают
# на дерево релиза, потом возвращаются. Dev и prod ставятся рядом на один
# телефон: у dev имя пакета с суффиксом .dev.
#
# Всё продуктовое — сервер, проект сборки, адрес выкладки, префикс имён —
# живёт ВНЕ репозитория (он публичный): ~/.config/lvn/release.env
# (или файл из LVN_RELEASE_ENV). Пароль сервера — в SSHPASS окружения.
set -u -o pipefail
CH="${1:-}"
ENVF="${LVN_RELEASE_ENV:-$HOME/.config/lvn/release.env}"
[ -f "$ENVF" ] || { echo "нет $ENVF — заведите: RELEASE_SERVER, RELEASE_PROJECT, RELEASE_DL, RELEASE_URL, RELEASE_PREFIX, RELEASE_LATEST_PROD, RELEASE_LATEST_DEV"; exit 2; }
# shellcheck disable=SC1090
. "$ENVF"
: "${RELEASE_SERVER:?}" "${RELEASE_PROJECT:?}" "${RELEASE_DL:?}" "${RELEASE_URL:?}" "${RELEASE_PREFIX:?}" "${RELEASE_LATEST_PROD:?}" "${RELEASE_LATEST_DEV:?}"
case "$CH" in
  dev)  REF=dev;  PREFIX="$RELEASE_PREFIX-dev"; LATEST="$RELEASE_LATEST_DEV";  SUFFIX=.dev ;;
  prod) REF=main; PREFIX="$RELEASE_PREFIX";     LATEST="$RELEASE_LATEST_PROD"; SUFFIX= ;;
  *) echo "usage: SSHPASS='…' qa/release.sh dev|prod"; exit 2 ;;
esac
[ -n "${SSHPASS:-}" ] || { echo "нужен SSHPASS в окружении (пароль сервера)"; exit 2; }

REPO="$(cd "$(dirname "$0")/.." && pwd)"
TREE="$HOME/ominis/builds/release-$CH"
PROJ="$RELEASE_PROJECT"
UNITY=/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity
AAPT=$(ls /Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/build-tools/*/aapt2 2>/dev/null | head -1)
SSH="sshpass -e ssh -o StrictHostKeyChecking=no -o PreferredAuthentications=password -o PubkeyAuthentication=no"
SCP="sshpass -e scp -q -o StrictHostKeyChecking=no -o PreferredAuthentications=password -o PubkeyAuthentication=no"

say() { printf '%s %s\n' "$(date +%H:%M:%S)" "$*"; }
die() { say "СТОП: $*"; exit 1; }

# ── 1. дерево релиза = ровно origin/<ветка> ──────────────────────────────
git -C "$REPO" fetch -q origin || die "fetch не прошёл"
git -C "$REPO" worktree prune
if [ ! -d "$TREE/.git" ] && [ ! -f "$TREE/.git" ]; then
  git -C "$REPO" worktree add -q --detach "$TREE" "origin/$REF" || die "не создать дерево $TREE"
else
  git -C "$TREE" switch -q --detach "origin/$REF" || die "не переключить дерево на origin/$REF"
fi
[ -z "$(git -C "$TREE" status --porcelain)" ] || die "в дереве релиза есть незакоммиченное — так не бывает, разберись"
ln -sfn "$REPO/sandbox/Library" "$TREE/sandbox/Library"   # Roslyn-проверке нужна Library с DLL
SHA=$(git -C "$TREE" rev-parse --short HEAD)
STAMP=$(date +%Y%m%d-%H%M)
NAME="$PREFIX-$STAMP"
say "релиз $CH: origin/$REF = $SHA → $NAME"

# ── 2. три проверки ───────────────────────────────────────────────────────
say "компиляция C# (Roslyn)…"
( cd "$TREE" && qa/csharp-check.sh > "$HOME/ominis/builds/$NAME.csharp.log" 2>&1 ) || die "C# не собирается — $HOME/ominis/builds/$NAME.csharp.log"
say "Go-стражи…"
( cd "$TREE/tools/lvnconv" && go test ./lvn/ -count=1 > "$HOME/ominis/builds/$NAME.guards.log" 2>&1 ) || {
  grep "^--- FAIL" "$HOME/ominis/builds/$NAME.guards.log" | head; die "стражи красные — $HOME/ominis/builds/$NAME.guards.log"; }
say "стенд утечек…"
RANGE=$([ "$CH" = dev ] && echo "origin/main..HEAD" || echo "HEAD~30..HEAD")
( cd "$TREE" && qa/leak-scan.sh "$RANGE" > "$HOME/ominis/builds/$NAME.leaks.log" 2>&1 ) || die "стенд утечек не пускает — $HOME/ominis/builds/$NAME.leaks.log"
tail -1 "$HOME/ominis/builds/$NAME.leaks.log"

# ── 3. сборка в родном проекте, пакеты — из дерева релиза ────────────────
if ps -axo args= | grep "Unity.app/Contents/MacOS/Unity" | grep -v grep | grep -q -- "$PROJ"; then
  die "редактор держит проект сборки — закрой его и повтори"; fi
MANIFEST="$PROJ/Packages/manifest.json"
cp "$MANIFEST" "$MANIFEST.release-bak"
restore() { [ -f "$MANIFEST.release-bak" ] && mv -f "$MANIFEST.release-bak" "$MANIFEST" && say "пакеты проекта возвращены на рабочее дерево"; }
trap restore EXIT
sed -i '' "s#file:$REPO/unity/Packages/#file:$TREE/unity/Packages/#g" "$MANIFEST"
grep -q "file:$TREE/unity/Packages/com.lvn.engine\"" "$MANIFEST" || die "не удалось перенаправить пакеты на $TREE"
OUT="$HOME/ominis/builds/$NAME.apk"; LOG="$HOME/ominis/builds/$NAME.log"
say "Unity batchmode → $OUT"
LVN_BUILD_OUT="$OUT" LVN_BUILD_COMMIT="$SHA" LVN_BUILD_CHANNEL="$CH" LVN_APP_ID_SUFFIX="$SUFFIX" \
  "$UNITY" -batchmode -quit -projectPath "$PROJ" -buildTarget Android \
  -executeMethod Lvn.EditorTools.CliBuild.Android -logFile "$LOG"; RC=$?
restore; trap - EXIT
[ -s "$OUT" ] || { grep -n "error CS\|Exception\|\[lvn-build\]" "$LOG" | head -20; die "APK не собрался (exit $RC) — $LOG"; }

# ── 4. манифест APK: тема, ярлык, диплинки, имя пакета ───────────────────
B=$("$AAPT" dump badging "$OUT" 2>/dev/null)
PKG=$(echo "$B" | grep -o "package: name='[^']*'" | sed "s/.*'\(.*\)'/\1/")
VER=$(echo "$B" | grep -o "versionName='[^']*'" | head -1)
X=$("$AAPT" dump xmltree "$OUT" --file AndroidManifest.xml 2>/dev/null)
THEME=$(echo "$X" | grep -o "android:theme[^ ]*=@0x7f[0-9a-f]*" | head -1 | sed 's/.*=//')
L=$(echo "$X" | grep -c LAUNCHER); SCH=$(echo "$X" | grep -c "android:scheme")
say "APK: $PKG $VER · LAUNCHER $L · диплинков $SCH · тема $THEME · $(stat -f %z "$OUT") байт"
[ "$L" = "1" ] && [ "$SCH" = "2" ] && [ -n "$THEME" ] || die "манифест APK не прошёл проверку"
case "$CH" in dev) [[ "$PKG" == *.dev ]] || die "у dev-сборки пакет без .dev: $PKG";; prod) [[ "$PKG" != *.dev ]] || die "у prod-сборки пакет с .dev: $PKG";; esac

# ── 5. выкладка ───────────────────────────────────────────────────────────
$SCP "$OUT" "$RELEASE_SERVER:/tmp/$NAME.apk" 2>&1 | grep -v Warning
$SSH "$RELEASE_SERVER" "O=\$(stat -c %U:%G $RELEASE_DL/$RELEASE_LATEST_PROD); install -o \${O%%:*} -g \${O##*:} -m 644 /tmp/$NAME.apk $RELEASE_DL/$NAME.apk && cp -p $RELEASE_DL/$NAME.apk $RELEASE_DL/$LATEST && rm -f /tmp/$NAME.apk; for f in $NAME.apk $LATEST; do echo \"\$f http=\$(curl -s -o /dev/null -w %{http_code} $RELEASE_URL/\$f) байт=\$(stat -c %s $RELEASE_DL/\$f)\"; done" 2>&1 | grep -v Warning
# ── 6. карточка сборки для сниппета в мессенджере ─────────────────────────
# Ссылка на .apk — двоичный файл, сниппета у неё нет. Боту-превью nginx отдаёт
# вместо файла HTML с OG-тегами и картинку 1200×630 со списком изменений
# (qa/release-preview.py); людям по той же ссылке — сам APK. Список — темы
# коммитов с прошлого выпуска этого канала (releases.log), служебные
# (docs/chore/test/merge) опускаются, пока есть содержательные.
PREV=$(awk -v ch="$CH" '$2==ch {sha=$3} END{print sha}' "$HOME/ominis/builds/releases.log" 2>/dev/null)
NOTES="${RELEASE_NOTES:-}"
if [ -n "$NOTES" ] && [ -f "$NOTES" ]; then LINES=$(grep -v '^\s*$' "$NOTES")
else
  RANGE_LOG=$([ -n "$PREV" ] && git -C "$TREE" cat-file -e "$PREV^{commit}" 2>/dev/null && echo "$PREV..HEAD" || echo "-12")
  ALL=$(git -C "$TREE" log --no-merges --format=%s $RANGE_LOG)
  MEAT=$(echo "$ALL" | grep -Ev '^(docs|chore|test|tests|ci|merge)(\(|:)' || true)
  LINES=$(echo "${MEAT:-$ALL}" | sed -E 's/^[a-z]+\(([^)]*)\): /\1: /; s/^[a-z]+: //' | head -12)
fi
PREV_DIR="$HOME/ominis/builds/preview"; mkdir -p "$PREV_DIR"
SIZE_MB=$(( $(stat -f %z "$OUT") / 1048576 ))
TITLE="${RELEASE_TITLE:-Сборка} · $CH"
SUB="$(date '+%d.%m %H:%M') · коммит $SHA · $SIZE_MB МБ$([ "$CH" = dev ] && echo ' · пакет .dev')"
echo "$LINES" | python3 "$REPO/qa/release-preview.py" --out "$PREV_DIR" --name "$NAME" --title "$TITLE" --subtitle "$SUB" \
  --apk "$RELEASE_URL/$NAME.apk" --page "${RELEASE_URL%/*}/dl-preview/$NAME.html" >/dev/null || say "карточка не собралась — сниппета не будет"
LBASE="${LATEST%.apk}"
cp -f "$PREV_DIR/$NAME.png" "$PREV_DIR/$LBASE.png" 2>/dev/null
sed "s#dl-preview/$NAME#dl-preview/$LBASE#g; s#/$NAME.apk#/$LATEST#g" "$PREV_DIR/$NAME.html" > "$PREV_DIR/$LBASE.html" 2>/dev/null
$SCP "$PREV_DIR/$NAME.html" "$PREV_DIR/$NAME.png" "$PREV_DIR/$LBASE.html" "$PREV_DIR/$LBASE.png" "$RELEASE_SERVER:${RELEASE_DL%/*}/dl-preview/" 2>&1 | grep -v Warning
$SSH "$RELEASE_SERVER" "chown --reference=$RELEASE_DL/$RELEASE_LATEST_PROD ${RELEASE_DL%/*}/dl-preview/$NAME.* ${RELEASE_DL%/*}/dl-preview/$LBASE.* 2>/dev/null" 2>&1 | grep -v Warning
say "карточка: ${RELEASE_URL%/*}/dl-preview/$NAME.html"

SUM=$(shasum -a 256 "$OUT" | cut -c1-16)
echo "$(date '+%Y-%m-%d %H:%M') $CH $SHA $NAME.apk $PKG sha256:$SUM" >> "$HOME/ominis/builds/releases.log"
say "ГОТОВО $CH · коммит $SHA · $RELEASE_URL/$NAME.apk (она же $RELEASE_URL/$LATEST)"
