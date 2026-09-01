package main

import (
	"encoding/json"
	"testing"
)

// КРУГ ФАЙЛА СОСТОЯНИЯ: записали — прочитали — то же самое.
//
// Состояние игрока лежит на диске обёрнутым: версия рядом с документом. Обе
// половины писались порознь, и проверялась ни одна — при том что асимметрия
// здесь означает потерянный прогресс, причём молча.
func TestStateFileRoundTrip(t *testing.T) {
	for _, doc := range []string{
		`{"Way":{"Moral":3},"имя":"Виктория"}`,
		`{}`,
		`{"n":0,"flag":false,"s":""}`,
	} {
		in := stateEntry{body: json.RawMessage(doc), version: 7}
		out := decodeStateFile(encodeStateFile(in))
		if out.version != 7 {
			t.Errorf("версия потерялась: %d вместо 7 (%s)", out.version, doc)
		}
		if !json.Valid(out.body) || string(out.body) != doc {
			t.Errorf("документ изменился:\n  было  %s\n  стало %s", doc, out.body)
		}
	}
}

// СТАРЫЙ ФАЙЛ — ГОЛЫЙ ДОКУМЕНТ, и узнавать его надо ПО ОБЁРТКЕ, а не по
// наличию поля с подходящим именем.
//
// Автор вправе завести переменную `doc`: это обычное слово. Документ
// `{"doc":…, "score":5}` — не обёртка, а состояние игрока, и прочитанный как
// обёртка он теряет ВСЁ, кроме одного поля. Ни ошибки, ни строки в логе:
// игрок просто возвращается с обнулённым прогрессом.
func TestLegacyDocWithADocFieldIsNotAWrapper(t *testing.T) {
	legacy := `{"doc":{"chapter":2},"score":5}`
	out := decodeStateFile([]byte(legacy))
	if string(out.body) != legacy {
		t.Errorf("старый документ приняли за обёртку и потеряли остальное:\n"+
			"  было  %s\n  стало %s", legacy, out.body)
	}
	if out.version != 0 {
		t.Errorf("у старого файла версии нет, а получили %d", out.version)
	}
}
