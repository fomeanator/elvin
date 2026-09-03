#!/usr/bin/env bash
# СНИМОК ТОГО, ЧТО НЕЛЬЗЯ ПЕРЕСОБРАТЬ.
#
# Контент пересобирается импортом, бинарь — сборкой, панель — npm. А кошельки,
# аккаунты, сейвы, таблицы результатов и учётки панели существуют в одном
# экземпляре: пропали — восстановить неоткуда. До 03.09.2026 бэкапа не было
# вовсе, и потеря диска стоила бы всех покупок и всего прогресса игроков.
#
# Три вещи делают этот скрипт полезным, а не ритуальным:
#
#   1. База копируется СВОИМ способом (sqlite3 .backup), а не cp. Копия
#      живого файла рядом с недописанным WAL — это копия, которая может не
#      открыться, и узнают об этом ровно в тот день, когда она понадобится.
#   2. Дневники клиента исключены. Их 189 МБ против одного мегабайта всего
#      остального, они восстановлению не подлежат по смыслу (диагностика) и
#      превратили бы ежедневный снимок в ежедневные 200 МБ.
#   3. Снимок ПРОВЕРЯЕТСЯ (tar -t) до того, как уедет старый: битый архив,
#      вытеснивший исправный, хуже отсутствия бэкапа.
#
# Запускается таймером systemd (lvn-backup.timer), ставит его setup.sh.
# Восстановление — руками и осознанно: остановить lvn, распаковать в
# content/, вернуть владельца lvn:lvn, запустить.
#
#   LVN_HOME=/srv/lvn KEEP=14 deploy/backup.sh
set -euo pipefail

LVN_HOME="${LVN_HOME:-/srv/lvn}"
CONTENT="${CONTENT:-$LVN_HOME/content}"
DEST="${DEST:-$LVN_HOME/backups}"
KEEP="${KEEP:-14}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
log() { echo "[lvn-backup] $*"; }

[ -d "$CONTENT" ] || { echo "нет каталога контента: $CONTENT" >&2; exit 1; }
mkdir -p "$DEST"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
SNAP="$WORK/snapshot"
mkdir -p "$SNAP"

# ── 1. Собрать снимок ──────────────────────────────────────────────────────
# Копируем, а не архивируем на месте: всё вместе весит около мегабайта, зато
# видно ровно то, что уедет, и tar не спорит с файлами, которые пишутся прямо
# сейчас.
if [ -d "$CONTENT/services" ]; then
  mkdir -p "$SNAP/services"
  # Дневники клиента — диагностика, а не данные (см. шапку). База — ниже,
  # своим способом.
  find "$CONTENT/services" -mindepth 1 -maxdepth 1 \
    ! -name client-logs ! -name 'lvn.db' ! -name 'lvn.db-wal' ! -name 'lvn.db-shm' \
    -exec cp -a {} "$SNAP/services/" \;
fi
[ -d "$CONTENT/state" ] && cp -a "$CONTENT/state" "$SNAP/state"

DB="$CONTENT/services/lvn.db"
if [ -f "$DB" ]; then
  mkdir -p "$SNAP/services"
  if command -v sqlite3 >/dev/null; then
    sqlite3 "$DB" ".backup '$SNAP/services/lvn.db'"
    ok="$(sqlite3 "$SNAP/services/lvn.db" 'PRAGMA integrity_check;' || echo failed)"
    [ "$ok" = "ok" ] || { echo "снимок базы не прошёл проверку: $ok" >&2; exit 1; }
  else
    # Без sqlite3 честнее увезти всю тройку (db + wal + shm), чем один файл:
    # восстановление из неё — обычный путь SQLite, из одинокого .db — нет.
    log "ВНИМАНИЕ: sqlite3 не установлен — копирую db+wal+shm как есть"
    for f in "$DB" "$DB-wal" "$DB-shm"; do
      [ -f "$f" ] && cp -a "$f" "$SNAP/services/"
    done
  fi
fi

# ── 2. Упаковать и ПРОВЕРИТЬ до того, как вытеснять старое ─────────────────
ARCHIVE="$DEST/lvn-$STAMP.tar.gz"
tar -czf "$ARCHIVE.part" -C "$SNAP" .
tar -tzf "$ARCHIVE.part" >/dev/null
mv "$ARCHIVE.part" "$ARCHIVE"
chmod 600 "$ARCHIVE"
log "снимок готов: $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"

# ── 3. Ротация ─────────────────────────────────────────────────────────────
ls -1t "$DEST"/lvn-*.tar.gz 2>/dev/null | tail -n +$((KEEP + 1)) | while read -r old; do
  rm -f "$old" && log "убран старый снимок: $(basename "$old")"
done
