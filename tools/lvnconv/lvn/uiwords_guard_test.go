package lvn

import (
	"encoding/json"
	"flag"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// РЕЕСТР ПОДПИСЕЙ НЕ СМЕЕТ ОТСТАТЬ ОТ КОДА.
//
// Реестр (`ui-words.json`) вшит в инструмент и служит автору шаблоном словаря:
// по нему он узнаёт, что вообще надо перевести. Отставший реестр молчит ровно
// про новые подписи — то есть ровно про те, которых в чужом словаре ещё нет.
// Живой случай: ключ очереди загрузок появился в коде, в словарь не попал, и
// «Downloading 0 of 7» уехало игроку.
//
// Чинится генератором: `go test ./lvn -run TestUiWordsRegistry -update`.
var updateRegistry = flag.Bool("update", false, "перезаписать ui-words.json по коду")

func TestUiWordsRegistryMatchesCode(t *testing.T) {
	root := repoRoot(t)
	scanned, err := ScanUiWords(root)
	if err != nil {
		t.Fatalf("разбор исходников: %v", err)
	}
	// Промахнись якорь разбора — реестр «совпал» бы с пустотой.
	atLeast(t, len(scanned), 100, "подписей движка")

	path := filepath.Join(root, "tools", "lvnconv", "lvn", "ui-words.json")
	if *updateRegistry {
		data, _ := json.MarshalIndent(scanned, "", " ")
		if err := os.WriteFile(path, append(data, '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
		t.Logf("реестр перезаписан: %d подписей", len(scanned))
		return
	}

	have := UiWords()
	index := map[string]UiWord{}
	for _, w := range have {
		index[w.Key] = w
	}
	var added, changed []string
	for _, w := range scanned {
		old, ok := index[w.Key]
		if !ok {
			added = append(added, w.Key)
			continue
		}
		if old.Default != w.Default || old.Field != w.Field {
			changed = append(changed, w.Key)
		}
		delete(index, w.Key)
	}
	var gone []string
	for k := range index {
		gone = append(gone, k)
	}
	if len(added)+len(changed)+len(gone) > 0 {
		t.Fatalf("реестр подписей разошёлся с кодом:\n  новых: %s\n  изменились: %s\n  исчезли: %s\n\n"+
			"Почините генератором: go test ./lvn -run TestUiWordsRegistry -update",
			strings.Join(added, ", "), strings.Join(changed, ", "), strings.Join(gone, ", "))
	}
}
