#!/usr/bin/env python3
"""Validate Unity NUnit output; a filtered run is not full-suite acceptance."""
import sys
import xml.etree.ElementTree as ET


def report(path, platform, floor, test_filter=""):
    try:
        root = ET.parse(path).getroot()
        total, passed, failed = (int(root.attrib[k]) for k in ("total", "passed", "failed"))
    except (OSError, ET.ParseError, KeyError, ValueError) as error:
        print(f"  {platform}: не удалось прочитать результаты ({error})")
        return 1
    cases = list(root.iter("test-case"))
    skipped = [c for c in cases if c.get("result") == "Skipped"]
    print(f"  {platform}: {passed}/{total} passed, {failed} failed, {len(skipped)} skipped")
    for case in skipped[:10]:
        why = (case.findtext("reason/message") or "причина не названа").strip().splitlines()
        print("    skipped:", case.get("name"), "—", why[0][:80] if why else "")
    for case in cases:
        if case.get("result") not in ("Passed", "Skipped"):
            print("   ", case.get("result"), case.get("fullname"))
    if total <= 0 or passed <= 0 or len(cases) != total:
        print("    ПУСТОЙ ИЛИ НЕПОЛНЫЙ ПРОГОН: результат не подтверждает выполнение тестов")
        return 1
    if failed or root.get("result") not in ("Passed", "Skipped", "Skipped:Ignored"):
        return 1
    if any(c.get("result") not in ("Passed", "Skipped") for c in cases):
        return 1
    external = sum(any(p.get("name") == "Category" and p.get("value") == "LvnExternalContent"
                       for p in c.findall("properties/property")) for c in cases)
    required = total - external
    if external:
        print(f"    Внешний контент: {external}; обязательный набор: {required}")
    if test_filter:
        print(f"    Выборочная проверка: {test_filter}; полный набор не проверялся")
    elif required < floor:
        print(f"    ТЕСТОВ МЕНЬШЕ ПОЛА: {required} при {floor} — проверки ИСЧЕЗЛИ")
        return 1
    elif required > floor:
        print(f"    (тестов стало больше: {required} при поле {floor} — поднимите пол)")
    return 0


if __name__ == "__main__":
    sys.exit(report(sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4] if len(sys.argv) > 4 else ""))
