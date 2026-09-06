#!/usr/bin/env bash
# СУДЬЯ ЖДЁТ ЧУЖОЙ ПРОГОН ПЕРЕД КАЖДЫМ ЗАПУСКОМ UNITY, А НЕ ОДИН РАЗ.
#
# Замер 06.09. Ожидание чужого batchmode стояло ОДИН РАЗ в начале run-all.sh,
# а фаза Unity наступает строк через семьсот и минут через десять. Второй
# прогон, стартовавший, пока первый мелет стенды и Go, ждать некого не видит —
# batchmode ещё не запущен, — и к Unity оба подходят разом. Второй умирает, не
# написав результатов, а последними строками его лога оказываются лицензионные:
# из-за этого дважды был сделан ложный вывод «Unity упёрлась в Licensing 505».
# Доказательство: прогон 19:44 стартовал Unity в 19:51 внутри чужой фазы
# EditMode 19:46–19:54, а те же строки про 505 есть в прогоне, который PASS.
#
# Проверяются ДВЕ вещи, и обе — без подделки процессов:
#
#   ШАБЛОН    настоящая командная строка Unity на ЭТОМ TestHost подходит, а
#             сам pgrep, этот скрипт, оболочка с теми же словами и Unity на
#             ЧУЖОМ проекте — нет. Старый шаблон `batchmode.*TestHost` ловил
#             любую строку с обоими словами: живьём он поймал командную строку
#             агента, в которой эти слова просто упомянуты, и объявил TestHost
#             занятым.
#   СОСЕДСТВО каждый запуск Unity на TestHost имеет ожидание в предыдущих
#             строках. Это ровно та регрессия, из которой дефект вырос:
#             ожидание было, но не там.
#
# Подделка процесса не используется намеренно: чтобы pgrep увидел имя «Unity»,
# процессу нужен такой файл (macOS игнорирует подменённый argv[0]), а копия
# /bin/sh под этим именем на этой системе при любом сигнале застревает в
# состоянии UE — kill -9 не берёт, живёт до перезагрузки. Стенд, отравляющий
# машину призраками, хуже отсутствующего.
#
#   qa/testhost-wait-check.sh [-bite]
#
# -bite убирает ожидание перед фазами Unity: стенд обязан это заметить.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

python3 - "$PWD/qa/run-all.sh" "${BITE:-}" <<'PY'
import re, subprocess, sys, tempfile, os
from pathlib import Path

путь, укус = sys.argv[1], bool(sys.argv[2])
исходник = Path(путь).read_text()
корень = str(Path(путь).parent.parent)
плохо = []

# ── 1. ШАБЛОН ───────────────────────────────────────────────────────────────
# Определения берём из САМОГО судьи и исполняем его же оболочкой: копия
# шаблона в стенде разошлась бы с оригиналом молча.
куски = исходник.split('# Другой batchmode на TestHost', 1)
if len(куски) < 2:
    print('FAIL: в судье не нашлось блока ожидания TestHost'); sys.exit(1)
опред = куски[1].split('wait_for_testhost() {', 1)[0]
проба = tempfile.NamedTemporaryFile('w', suffix='.sh', delete=False)
проба.write('REPO_ROOT="$1"\n' + опред +
            '\nprintf "%s" "$TESTHOST_BATCH_RE"\n')
проба.close()
готово = subprocess.run(['bash', проба.name, корень], capture_output=True, text=True)
os.unlink(проба.name)
шаблон = готово.stdout.strip()
if not шаблон:
    print(f'FAIL: судья не отдал шаблон: {готово.stderr.strip()[:200]}'); sys.exit(1)

unity = '/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity'
случаи = [
    (f'{unity} -batchmode -nographics -projectPath {корень}/unity/TestHost -runTests', True),
    (f'{unity} -batchmode -projectPath {корень}/unity/TestHost -runTests -testPlatform PlayMode', True),
    # Чужой проект — не наше дело.
    (f'{unity} -batchmode -projectPath /Users/x/other/unity/TestHost -runTests', False),
    # Редактор без batchmode — им судья не занимается здесь.
    (f'{unity} -projectpath {корень}/unity/TestHost -useHub', False),
    # Сам pgrep со шаблоном в аргументах.
    (f'pgrep -f -- {шаблон}', False),
    # Оболочка, в чьей командной строке ПРОСТО УПОМЯНУТЫ те же слова: живой
    # случай — командная строка агента с заданием про batchmode и TestHost.
    (f'/bin/zsh -c codex exec "почини batchmode на {корень}/unity/TestHost"', False),
    # Файл с похожим именем.
    (f'/tmp/x/NotUnity -batchmode -projectPath {корень}/unity/TestHost', False),
]
for строка, ждём in случаи:
    совпало = subprocess.run(['grep', '-Eq', '--', шаблон],
                             input=строка + '\n', text=True).returncode == 0
    if совпало != ждём:
        плохо.append(('ловит' if совпало else 'не ловит') + ': ' + строка[:110])
if not плохо:
    print('  шаблон: своя Unity подходит; pgrep, оболочка, чужой проект и NotUnity — нет')

# ── 2. СОСЕДСТВО ────────────────────────────────────────────────────────────
строки = исходник.splitlines()
if укус:
    строки = [s for s in строки if s.strip() != 'wait_for_testhost || exit 1']
пуски = [i for i, s in enumerate(строки)
         if '"$UNITY" "${args[@]}"' in s]
пуски = [i for i in пуски
         if any('unity/TestHost' in строки[j] for j in range(max(0, i - 12), i))]
if len(пуски) < 2:
    плохо.append(f'в судье найдено {len(пуски)} запусков Unity на TestHost — ожидалось два')
несторожёные = []
for i in пуски:
    окно = строки[max(0, i - 12):i]
    if not any('wait_for_testhost' in s for s in окно):
        несторожёные.append(i + 1)

if укус:
    if несторожёные:
        print(f'укус чист: без ожидания найдены запуски в строках {несторожёные}')
        sys.exit(0)
    print('СТЕНД СЛЕП: ожидание убрано, а стенд этого не заметил'); sys.exit(2)

if несторожёные:
    плохо.append('запуск Unity без ожидания в предыдущих строках: ' + str(несторожёные))
else:
    print(f'  соседство: оба запуска Unity ({len(пуски)}) ждут TestHost непосредственно перед стартом')

if плохо:
    print('РВЁТСЯ:\n  ' + '\n  '.join(плохо)); sys.exit(1)
print('держит: шаблон различает своё и чужое, ожидание стоит перед каждым запуском Unity')
PY
