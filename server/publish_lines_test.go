package main

// АВТОРУ — СТРОКА, И ОДНА НА ВСЕ СТАДИИ.
//
// Публикацию через API делает ИИ-агент по файлу-инструкции: прислал текст
// главы, получил ответ. Если в ответе адрес «script[2]», агенту (как и
// человеку) некуда идти: файла с таким номером команды он не писал.
//
// Замер 05.09 на живом сервере: ошибка КОМПИЛЯЦИИ честно отвечала «line 5:
// unclosed for/while block», а структурная в соседнем ответе — «script[2]».
// Один запрос, два языка адресации. Теперь адрес один.

import (
	"strings"
	"testing"
)

func TestPublishAddressesTheAuthorsLine(t *testing.T) {
	s := publishSrv(t)

	// Строки: 1 — scene, 3 — реплика, 5 — выбор с висячим переходом.
	code, out := publish(t, s, map[string]any{
		"id": "proba", "name": "Проба", "chapter": 1,
		"lvns": "scene proba\n\nПервая реплика.\n\n- Уйти -> нетакой\n",
	})
	if code != 422 {
		t.Fatalf("глава с висячим переходом опубликовалась: %d %v", code, out)
	}
	errs := errorStrings(out["errors"])
	if len(errs) == 0 {
		t.Fatal("ошибок нет — проверять адрес не на чем")
	}
	for _, e := range errs {
		if strings.Contains(e, "script[") {
			t.Errorf("находка адресована номером команды: %q", e)
		}
		if !strings.Contains(e, "line 5") {
			t.Errorf("ждали адрес «line 5» (там написан переход), получили %q", e)
		}
	}

	// Предупреждение при УСПЕШНОЙ публикации адресуется так же: агент читает
	// его тем же способом, что и ошибку.
	code, out = publish(t, s, map[string]any{
		"id": "proba", "name": "Проба", "chapter": 2,
		"lvns": "scene proba\n\nПервая.\nset ключ = 1\n-> __end\n",
	})
	if code != 200 {
		t.Fatalf("глава с предупреждением не опубликовалась: %d %v", code, out)
	}
	warns := errorStrings(out["warnings"])
	if len(warns) == 0 {
		t.Fatal("предупреждений нет — проверять адрес не на чем")
	}
	for _, w := range warns {
		if strings.Contains(w, "script[") {
			t.Errorf("предупреждение адресовано номером команды: %q", w)
		}
		if !strings.Contains(w, "line 4") {
			t.Errorf("ждали адрес «line 4», получили %q", w)
		}
	}

	// А ошибка компиляции говорила строкой и раньше — проверяем, что формат у
	// них ОДИН: агент не должен разбирать два разных языка.
	code, out = publish(t, s, map[string]any{
		"id": "proba", "name": "Проба", "chapter": 3,
		"lvns": "scene proba\n\nПервая.\n\nif сила > 5 {\n  Вторая.\n",
	})
	if code != 400 {
		t.Fatalf("незакрытый блок не отвергнут: %d %v", code, out)
	}
	if msg, _ := out["error"].(string); !strings.HasPrefix(msg, "line ") {
		t.Errorf("ошибка компиляции без адреса строки: %q", msg)
	}
}

// errorStrings достаёт список строк из ответа (JSON отдаёт []any).
func errorStrings(v any) []string {
	arr, _ := v.([]any)
	out := make([]string, 0, len(arr))
	for _, e := range arr {
		if s, ok := e.(string); ok {
			out = append(out, s)
		}
	}
	return out
}
