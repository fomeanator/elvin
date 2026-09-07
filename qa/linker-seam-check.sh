#!/usr/bin/env bash
# СБОРКА, НА КОТОРУЮ НИКТО НЕ ССЫЛАЕТСЯ, ДЛЯ ЛИНКЕРА МЕРТВА.
#
# Необязательные части движка подключены ШВОМ: ядро о них не знает, а
# реализация цепляется сама из [RuntimeInitializeOnLoadMethod] и заполняет
# делегаты моста. Ссылок на неё в коде нет ни одной — в этом и смысл шва.
# Но ровно поэтому UnityLinker считает такую сборку недостижимой и
# выбрасывает целиком: в редакторе всё работает, в APK ничего нет.
#
# Замер 07.09: в собранном APK был LvnSpineBridge и НИ ОДНОГО типа
# Lvn.Engine.Spine, spine-unity, spine-csharp. Список сохраняемых сборок
# отдаётся официальным швом сборки (Lvn.EditorTools.LinkerPreserve), потому
# что link.xml внутри UPM-пакета до линкера НЕ ДОЕЗЖАЕТ — проверено
# пересборкой: Unity его импортирует, а в аргументах линкера остаются только
# её собственные три файла.
#
# Стенд сверяет ТРИ стороны и не даёт им разойтись:
#   1) у каждого моста (класс с делегатами и Available) есть реализация;
#   2) сборка этой реализации перечислена в LinkerPreserve.SeamAssemblies;
#   3) КАЖДЫЙ шейдер, который код просит по имени через Shader.Find, назван в
#      ShaderPreserve.AskedByName — либо лежит рядом в Resources, откуда его
#      тянет ссылкой сам файл.
#
# Третья проверка добыта тем же способом, что и первая: замером с устройства.
# Сборка спайна уже доехала, а шейдер «Spine/SkeletonGraphic» — нет, и журнал
# дал «ArgumentNullException: Parameter name: shader». На экране это выглядело
# как «от актёра остался только задний фон». Имя шейдера написано в двух
# местах (просящий код и закрепляющий шов), потому что пакет необязательный и
# сослаться на константу нельзя, — значит расходиться им нельзя молча.
# Ловит он именно «завели новый необязательный пакет и забыли про линкер» —
# отказ, который на машине разработчика невидим.
#
#   qa/linker-seam-check.sh [-bite]
#
# -bite убирает спайн из списка сохраняемых и проверяет САМ СЕБЯ: заметил —
# выходит 0 (укус честен), не заметил — 2 (мерка слепа). Так же считает коды
# qa/bites-honest-check.sh, гоняющий все укусы разом.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

python3 - "${BITE:-}" <<'PY'
import re, sys
from pathlib import Path

укус = bool(sys.argv[1])
корень = Path('.')
плохо = []

шов = корень / 'unity/Packages/com.lvn.engine/Editor/LinkerPreserve.cs'
if not шов.exists():
    print('FAIL: нет шва линкера — Lvn.EditorTools.LinkerPreserve'); sys.exit(1)
текст = шов.read_text(encoding='utf-8')

# Список читаем из САМОГО шва, а не переписываем сюда: копия разошлась бы молча.
блок = re.search(r'SeamAssemblies\s*=\s*\{(.*?)\};', текст, re.S)
if not блок:
    print('FAIL: в шве не нашёлся список SeamAssemblies'); sys.exit(1)
сохраняем = set(re.findall(r'"([^"]+)"', блок.group(1)))
if укус:
    сохраняем.discard('Lvn.Engine.Spine')

# Шов обязан отдавать путь линкеру, а не просто писать файл.
if 'GenerateAdditionalLinkXmlFile' not in текст:
    плохо.append('шов не реализует GenerateAdditionalLinkXmlFile — линкер его не спросит')

# Необязательные пакеты движка: у каждого своя сборка (имя из asmdef).
пакеты = sorted(p for p in (корень / 'unity/Packages').glob('com.lvn.engine.*') if p.is_dir())
проверено = 0
for пакет in пакеты:
    for asmdef in пакет.rglob('*.asmdef'):
        текст_asm = asmdef.read_text(encoding='utf-8')
        имя = re.search(r'"name"\s*:\s*"([^"]+)"', текст_asm)
        if not имя:
            continue
        имя = имя.group(1)
        if имя.endswith('.Tests') or 'Editor' in имя:
            continue
        if not re.search(r'"defineConstraints"\s*:\s*\[\s*"', текст_asm):
            continue
        # ЗДЕСЬ ПРОХОДИТ ГРАНИЦА, и она не в «необязательности».
        #
        # Необязательная сборка, которую автор создаёт РУКАМИ (публичный класс,
        # `new AddressablesAssets()`), линкеру видна по ссылке из кода проекта —
        # её он сохранит сам. Опасна другая: та, что цепляется САМА, из
        # [RuntimeInitializeOnLoadMethod], и ссылок на себя не имеет ни одной.
        # Ровно её линкер и выбрасывает.
        #
        # Первая редакция стенда считала опасными все необязательные и краснела
        # на Addressables — на сборке, с которой всё в порядке. Признак взят по
        # тому, ЧТО ЛОМАЕТСЯ, а не по тому, что похоже.
        сам_цепляется = any('RuntimeInitializeOnLoadMethod' in f.read_text(encoding='utf-8')
                            for f in пакет.rglob('*.cs'))
        if not сам_цепляется:
            continue
        проверено += 1
        if имя not in сохраняем:
            плохо.append(f'{имя}: необязательная сборка не перечислена в SeamAssemblies — '
                         f'линкер выбросит её из сборки, и в редакторе это не видно')
        # Чужие рантаймы, на которые она ссылается, тоже держатся данными.
        for ссылка in re.findall(r'"(spine-[a-z]+)"', текст_asm):
            if ссылка not in сохраняем:
                плохо.append(f'{имя} ссылается на {ссылка}, а его в SeamAssemblies нет')

# ── ШЕЙДЕРЫ ПО ИМЕНИ ────────────────────────────────────────────────────────
шейдер_шов = корень / 'unity/Packages/com.lvn.engine/Editor/ShaderPreserve.cs'
if not шейдер_шов.exists():
    плохо.append('нет шва шейдеров — Lvn.EditorTools.ShaderPreserve')
else:
    т = шейдер_шов.read_text(encoding='utf-8')
    бл = re.search(r'AskedByName\s*=\s*\{(.*?)\};', т, re.S)
    закреплены = set(re.findall(r'"([^"]+)"', бл.group(1))) if бл else set()
    if укус:
        закреплены.discard('Spine/SkeletonGraphic')
    # ВСТРОЕННЫЕ В UNITY закреплять не нужно и нельзя: они есть в сборке
    # всегда, а движок и так перебирает их через ?? на случай другого
    # конвейера рендера. Граница здесь по ПРИЧИНЕ (кто поставляет шейдер), а
    # не по сходству: первая редакция краснела на Standard и Sprites/Default,
    # с которыми всё в порядке.
    встроенные = {
        'Standard': 'встроенный Unity, есть в любой сборке',
        'Sprites/Default': 'встроенный Unity, запасной для спрайтов',
        'Universal Render Pipeline/Lit': 'из конвейера URP, ставится вместе с ним',
        'HDRP/Lit': 'из конвейера HDRP, ставится вместе с ним',
    }
    просят = {}
    for пакет in (корень / 'unity/Packages').glob('com.lvn.engine*'):
        for f in пакет.rglob('*.cs'):
            if '/Tests/' in str(f) or f.name == 'ShaderPreserve.cs':
                continue
            текст_f = f.read_text(encoding='utf-8')
            for имя in re.findall(r'Shader\.Find\(\s*"([^"]+)"', текст_f):
                просят.setdefault(имя, []).append(f)
            for симв in re.findall(r'Shader\.Find\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\)', текст_f):
                m = re.search(r'\b' + re.escape(симв.split('.')[-1]) + r'\s*=\s*"([^"]+)"', текст_f)
                if m:
                    просят.setdefault(m.group(1), []).append(f)
    for имя, файлы in sorted(просят.items()):
        if имя in закреплены or имя in встроенные:
            continue
        # Свой шейдер движка лежит рядом в Resources — его тянет ссылкой сам
        # файл, и закреплять его не нужно (правило записано в LvnPortalLayer).
        свой = any('Resources.Load' in f.read_text(encoding='utf-8') for f in файлы)
        if свой:
            continue
        плохо.append('шейдер «' + имя + '» просят по имени (' + файлы[0].name +
                     '), а в сборку его никто не вносит: Shader.Find вернёт null')

if проверено == 0:
    print('FAIL: не нашлось ни одной необязательной сборки — стенд смотрит не туда')
    sys.exit(1)

if укус:
    # Режим самопроверки: охраняемое сломано нарочно. Ценно не «упал», а
    # «упал ИМЕННО НА ТОМ» — стенд, краснеющий по другой причине, честным не
    # считается.
    свой = [s for s in плохо if s.startswith('Lvn.Engine.Spine:') or 'Spine/SkeletonGraphic' in s]
    if свой:
        print('укус замечен:', свой[0])
        sys.exit(0)
    print('УКУС НЕ ЗАМЕЧЕН: спайн вынут из списка, а стенд молчит — мерка слепа')
    sys.exit(2)

if плохо:
    print(f'FAIL: швов проверено {проверено}')
    for s in плохо:
        print('   ', s)
    sys.exit(1)
print(f'OK: необязательных сборок {проверено}, все сохранены от линкера '
      f'({len(сохраняем)} имён в списке)')
PY
