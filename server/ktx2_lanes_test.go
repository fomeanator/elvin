package main

import (
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// recordingEncoder подменяет basisu: пишет пустой «код» и запоминает порядок.
type recordingEncoder struct {
	mu      sync.Mutex
	order   []string
	started chan struct{} // закрывается, когда работник взял ПЕРВЫЙ путь
	gate    chan struct{} // первый вызов ждёт открытия ворот — так очереди наполняются до разбора
	first   sync.Once
}

func newRecordingEncoder() *recordingEncoder {
	return &recordingEncoder{started: make(chan struct{}), gate: make(chan struct{})}
}

func (e *recordingEncoder) encode(src, out string) error {
	e.first.Do(func() {
		close(e.started)
		<-e.gate
	})
	e.mu.Lock()
	e.order = append(e.order, filepath.Base(out))
	e.mu.Unlock()
	return os.WriteFile(out, []byte("ktx2"), 0o644)
}

func (e *recordingEncoder) seen() []string {
	e.mu.Lock()
	defer e.mu.Unlock()
	return append([]string(nil), e.order...)
}

// ЖИВОЕ ВЫТЕСНЯЕТ ФОНОВОЕ.
//
// Прогрев ставит в очередь десяток файлов; пока работник занят первым, игрок
// просит СВОЙ. Его файл должен быть закодирован следующим — не после
// десятка чужих, и уж точно не выброшен (одна полоса на двоих отказывала
// живому запросу при полной очереди, и отказ никто не читал).
func TestЖивойЗапросОбгоняетПрогрев(t *testing.T) {
	// Двенадцать раундов: select между ДВУМЯ готовыми каналами в Go случаен,
	// и без явного приоритета живой путь выигрывал бы половину раундов —
	// один раунд такую подмену не поймает, двенадцать ловят наверняка.
	for round := 0; round < 12; round++ {
		dir := t.TempDir()
		tr := newKtx2TranscoderIdle(newDownscaler())
		enc := newRecordingEncoder()
		tr.encode = enc.encode

		var warm []string
		for _, n := range []string{"a", "b", "c", "d", "e", "f"} {
			writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", n+".png")), 8, 8)
			if !tr.enqueueWaiting(filepath.Join(dir, "bg", n+".ktx2")) {
				t.Fatalf("прогрев не поставил %s", n)
			}
			warm = append(warm, n+".ktx2")
		}
		go tr.worker()
		<-enc.started // работник взял первый путь прогрева и стоит у ворот. Теперь — игрок
		live := filepath.Join(dir, "ui", "canvas.ktx2")
		writeTestPNG(t, mkParent(t, filepath.Join(dir, "ui", "canvas.png")), 8, 8)
		if !tr.enqueue(live) {
			t.Fatal("живая полоса пуста, а запрос не принят")
		}
		close(enc.gate)

		deadline := time.Now().Add(5 * time.Second)
		for len(enc.seen()) < len(warm)+1 {
			if time.Now().After(deadline) {
				t.Fatalf("закодировано %v, ждали %d", enc.seen(), len(warm)+1)
			}
			time.Sleep(5 * time.Millisecond)
		}
		got := enc.seen()
		if got[0] != warm[0] || got[1] != "canvas.ktx2" {
			t.Fatalf("раунд %d, порядок %v: живой canvas.ktx2 должен идти сразу за начатым %s", round, got, warm[0])
		}
	}
}

// Файл, уже стоящий в прогреве, живой запрос ПРОДВИГАЕТ, а не «уже есть,
// ждите»: иначе игрок ждёт хвост очереди, в которой его файл — тысячный.
func TestЖивойЗапросПродвигаетСтоящийВПрогреве(t *testing.T) {
	dir := t.TempDir()
	tr := newKtx2TranscoderIdle(newDownscaler())
	enc := newRecordingEncoder()
	tr.encode = enc.encode

	names := []string{"a", "b", "c", "z"}
	for _, n := range names {
		writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", n+".png")), 8, 8)
		tr.enqueueWaiting(filepath.Join(dir, "bg", n+".ktx2"))
	}
	go tr.worker()
	<-enc.started
	if !tr.enqueue(filepath.Join(dir, "bg", "z.ktx2")) {
		t.Fatal("живой запрос на стоящий в прогреве файл отвергнут")
	}
	close(enc.gate)

	deadline := time.Now().Add(5 * time.Second)
	for len(enc.seen()) < 2 {
		if time.Now().After(deadline) {
			t.Fatalf("закодировано %v", enc.seen())
		}
		time.Sleep(10 * time.Millisecond)
	}
	if got := enc.seen(); got[1] != "z.ktx2" {
		t.Fatalf("порядок %v: z.ktx2 просили живьём — он должен идти вторым, а не последним", got)
	}
}

// ПЕРВЫЙ ЭКРАН ГРЕЕТСЯ ПЕРВЫМ: обход диска идёт по алфавиту, /ui/ последняя,
// а прогрев обязан ставить обшивку в очередь раньше арта глав.
func TestПрогревНачинаетСОбшивки(t *testing.T) {
	dir := t.TempDir()
	// 1024×2: обшивка ≥ коробки, кода ей положено; арт истории — любой.
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "ui", "canvas.png")), 1024, 2)
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "scene.png")), 8, 8)
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "art", "cg.png")), 8, 8)

	tr := newKtx2TranscoderIdle(newDownscaler())
	if tr.bin() == "" {
		// Без basisu прогрев честно молчит — этот вопрос уже проверен
		// в TestServerSaysWhenItCannotEncode; здесь нужен сам порядок.
		tr.binPath = "/bin/true"
	}
	tr.warmAll(dir)

	want := 3 * 4 // три исходника × четыре ступени
	deadline := time.Now().Add(5 * time.Second)
	for len(tr.queue) < want {
		if time.Now().After(deadline) {
			t.Fatalf("в очереди %d, ждали %d", len(tr.queue), want)
		}
		time.Sleep(10 * time.Millisecond)
	}
	var order []string
	for i := 0; i < want; i++ {
		order = append(order, filepath.ToSlash(<-tr.queue))
	}
	for i := 0; i < 4; i++ {
		if !ktx2ChromeFirst(order[i]) {
			t.Fatalf("позиция %d: %s — обшивка должна идти первой, порядок %v", i, order[i], order)
		}
	}
	// А внутри арта истории порядок обхода сохранён (SliceStable): папки по
	// алфавиту, ступени в порядке прогрева.
	var rest []string
	for _, p := range order[4:] {
		rest = append(rest, filepath.Base(p))
	}
	want2 := []string{"cg.ktx2", "cg@2k.ktx2", "cg@1440.ktx2", "cg@1k.ktx2",
		"scene.ktx2", "scene@2k.ktx2", "scene@1440.ktx2", "scene@1k.ktx2"}
	if strings.Join(rest, " ") != strings.Join(want2, " ") {
		t.Fatalf("порядок арта истории перемешан:\n есть %v\n ждали %v", rest, want2)
	}
}

// 404 НАЗЫВАЕТ ПРИЧИНУ. Снаружи (curl -I) должно быть видно, ПОЧЕМУ кода нет:
// нечем, нечего, не положено или «поставлено, зайдите позже».
func TestОтказВКодеНазываетПричину(t *testing.T) {
	h, dir := newKtx2TestServer(t)
	_, noBasisu := exec.LookPath("basisu")

	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "scene.png")), 8, 8)
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "tiny@mini.png")), 8, 8)
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "ui", "icon.png")), 8, 8)

	cases := map[string]string{
		"/content/bg/nothing.ktx2":   ktx2WhyNoSource,
		"/content/bg/tiny@mini.ktx2": ktx2WhyNotCoded,
		"/content/ui/icon.ktx2":      ktx2WhyNotCoded,
		"/content/bg/scene.ktx2":     ktx2WhyQueued,
	}
	if noBasisu != nil {
		for k := range cases {
			cases[k] = ktx2WhyNoEncoder // нечем — и это главнее всех остальных причин
		}
	}
	for path, want := range cases {
		rec := get(t, h, path)
		if rec.Code != http.StatusNotFound {
			t.Fatalf("%s: код %d, ждали 404", path, rec.Code)
		}
		if got := rec.Header().Get(ktx2WhyHeader); got != want {
			t.Fatalf("%s: %s=%q, ждали %q", path, ktx2WhyHeader, got, want)
		}
	}
}
