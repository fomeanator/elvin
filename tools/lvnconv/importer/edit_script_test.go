package importer

import (
	"encoding/json"
	"testing"
)

// КОНВЕРТ ПЕРЕПИСЫВАНИЯ: скрипт бывает завёрнут по-разному, и обратно его надо
// завернуть ТАК ЖЕ.
//
// `decodeScriptOps` отдаёт способ упаковки вместе с командами. Забудь его
// позвать — и документ с полями запишется голым списком: глава потеряет всё,
// что лежало вокруг команд, а заметить это можно только по игре, которая
// перестала знать про свои настройки.
func TestОбёрткаСкриптаНеТеряетсяПриПравке(t *testing.T) {
	doc := map[string]any{
		"title":  "Глава",
		"vars":   map[string]any{"hp": float64(10)},
		"script": []any{map[string]any{"op": "say", "text": "привет"}},
	}
	raw, _ := json.Marshal(doc)
	sf := &ScriptFile{Data: raw}

	editScript(sf, func(ops []map[string]any) ([]map[string]any, bool) {
		return append(ops, map[string]any{"op": "say", "text": "и пока"}), true
	})

	var got map[string]any
	if err := json.Unmarshal(sf.Data, &got); err != nil {
		t.Fatalf("после правки скрипт перестал быть документом: %v", err)
	}
	if got["title"] != "Глава" {
		t.Fatal("заголовок потерян — обёртку не вернули на место")
	}
	if got["vars"] == nil {
		t.Fatal("переменные главы потеряны — обёртку не вернули на место")
	}
	if n := len(got["script"].([]any)); n != 2 {
		t.Fatalf("команд стало %d, ждали 2", n)
	}
}

// Перезапись без перемен не бесплатна: она переупаковывает JSON и меняет файл
// на диске, а значит и его отпечаток в манифесте — игроки увидят обновление
// главы, в которой ничего не изменилось.
func TestБезПеременФайлНеТрогается(t *testing.T) {
	raw := []byte(`{"script":[{"op":"say","text":"привет"}]}`)
	sf := &ScriptFile{Data: raw}
	editScript(sf, func(ops []map[string]any) ([]map[string]any, bool) {
		return ops, false
	})
	if string(sf.Data) != string(raw) {
		t.Fatalf("файл переписан без нужды:\n было %s\n стало %s", raw, sf.Data)
	}
}

// Нечитаемый скрипт не должен становиться пустым: лучше оставить как есть и
// дать разбору сказать своё слово, чем молча стереть содержимое главы.
func TestНечитаемыйСкриптОстаётсяКакБыл(t *testing.T) {
	raw := []byte(`не json вовсе`)
	sf := &ScriptFile{Data: raw}
	editScript(sf, func(ops []map[string]any) ([]map[string]any, bool) {
		return nil, true
	})
	if string(sf.Data) != string(raw) {
		t.Fatalf("нечитаемый скрипт затёрт: %s", sf.Data)
	}
}
