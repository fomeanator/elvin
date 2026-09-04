package main

import (
	"os"
	"strings"
	"testing"
)

// ЗАМОК НА СЕЙВЕ: ПРОВЕРКА ДО РАБОТЫ, СРАВНЕНИЕ ПОСТОЯННОГО ВРЕМЕНИ, ХРАНИТСЯ ХЕШ.
//
// Адрес сейва угадывается: он складывается из идентификатора игрока и титула.
// Значит вся защита держится на ключе, который знает только устройство.
//
// Замерено живьём (qa/state-privacy-check.sh):
//
//	хозяин со своим ключом    200, документ отдан
//	чужой без ключа           401
//	чужой с чужим ключом      401
//	чужой пишет               401, и документ хозяина НЕ изменился
//
// Три свойства, каждое из которых легко потерять незаметно:
//
//	ДО РАБОТЫ   проверка стоит перед разбором метода. Отказ после чтения тела
//	            или после записи — это уже не отказ;
//	ПО ВРЕМЕНИ  сравнение постоянного времени. Обычное == на строках отвечает
//	            быстрее при первом же несовпавшем байте, и ключ подбирается
//	            побайтово, а не перебором;
//	ХЕШ         на диске лежит хеш, не сам ключ. Утёкший файл не должен
//	            открывать сейвы.
func TestSaveLockChecksBeforeItWorks(t *testing.T) {
	raw, err := os.ReadFile("main.go")
	if err != nil {
		t.Fatalf("обработчик состояния не прочитан: %v", err)
	}
	src := string(raw)

	const handler = "func (s *server) handleState("
	start := strings.Index(src, handler)
	if start < 0 {
		t.Fatal("обработчик состояния не найден — страж потерял предмет охраны")
	}
	body := src[start:]
	if end := strings.Index(body[len(handler):], "\nfunc "); end >= 0 {
		body = body[:len(handler)+end]
	}

	check := strings.Index(body, "stateKeyOK(")
	sw := strings.Index(body, "switch r.Method")
	if check < 0 {
		t.Fatal("проверка ключа исчезла из обработчика — сейвы стали общими")
	}
	if sw < 0 {
		t.Fatal("разбор метода не найден — страж смотрит не на тот код")
	}
	if check > sw {
		t.Error("ключ проверяется ПОСЛЕ разбора метода: отказ, наступивший после чтения " +
			"или записи, уже ничего не защищает")
	}
	// Проверка обязана касаться обоих методов, а не только записи.
	if strings.Contains(body, "MethodPut && !s.stateKeyOK") ||
		strings.Contains(body, "if r.Method == http.MethodPut {\n\t\tif !s.stateKeyOK") {
		t.Error("ключ спрашивают только на записи — чужой прочитает сейв целиком")
	}

	const fn = "func (s *server) stateKeyOK("
	at := strings.Index(src, fn)
	if at < 0 {
		t.Fatal("проверка ключа не найдена")
	}
	keyFn := src[at:]
	if end := strings.Index(keyFn[len(fn):], "\nfunc "); end >= 0 {
		keyFn = keyFn[:len(fn)+end]
	}
	if !strings.Contains(keyFn, "subtle.ConstantTimeCompare") {
		t.Error("ключ сравнивается обычным равенством — ответ приходит тем быстрее, " +
			"чем меньше совпало, и ключ подбирается побайтово")
	}
	if !strings.Contains(keyFn, "sha256.Sum256") {
		t.Error("на диск кладётся сам ключ, а не его хеш — утёкший файл откроет сейвы")
	}
}
