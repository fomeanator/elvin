package main

import (
	"runtime"
	"strings"
	"testing"
)

// ОГРАНИЧИТЕЛЬ ПОТОКОВ — ДОВОД ОКРУЖЕНИЯ, И «0» ЗНАЧИТ «БЕЗ ОГРАНИЧЕНИЯ».
//
// На него опирается деплой: setup.sh пишет LVN_KTX2_THREADS=0 на выделенный
// бокс. Если «0» однажды прочитается как «умолчание» (n > 0 вместо n >= 0),
// прод молча вернётся к четверти ядер, и очередь кодов снова поползёт
// часами — ровно то, что стоило полотна витрины 02.09.
func TestПотокиКодировщикаИзОкружения(t *testing.T) {
	def := runtime.NumCPU() / 4
	if def < 1 {
		def = 1
	}
	cases := []struct {
		env  string
		want int
	}{
		{"0", 0},
		{"3", 3},
		{"", def},
		{"abc", def},
		{"-2", def},
	}
	for _, c := range cases {
		t.Setenv("LVN_KTX2_THREADS", c.env)
		if got := ktx2EncodeThreads(); got != c.want {
			t.Errorf("LVN_KTX2_THREADS=%q: потоков %d, ждали %d", c.env, got, c.want)
		}
	}

	t.Setenv("LVN_KTX2_THREADS", "0")
	if line := strings.Join(ktx2EncoderArgs("a.png", "a.ktx2"), " "); strings.Contains(line, "-max_threads") {
		t.Errorf("при 0 кодировщик всё равно ограничен: %s", line)
	}
	t.Setenv("LVN_KTX2_THREADS", "3")
	if line := strings.Join(ktx2EncoderArgs("a.png", "a.ktx2"), " "); !strings.Contains(line, "-max_threads 3") {
		t.Errorf("при 3 ограничения нет: %s", line)
	}
	// Ориентация и мипы — часть договора с клиентом, не настройка.
	if line := strings.Join(ktx2EncoderArgs("a.png", "a.ktx2"), " "); !strings.Contains(line, "-y_flip") || !strings.Contains(line, "-mipmap") {
		t.Errorf("кодировщик потерял -y_flip/-mipmap: %s", line)
	}
}
