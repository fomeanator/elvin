package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// Одно падение в цикле даёт тысячу строк и выглядит как тысяча проблем.
// Группировка обязана свести их в одну — и ранжировать по ЛЮДЯМ, потому что
// редкое падение у половины игроков важнее частого у одного.
func TestCrashesGroupAndRankByPeople(t *testing.T) {
	dir := t.TempDir()
	day := time.Now().UTC().Format("2006-01-02")
	lines := ""
	// Шумное: один человек, сто повторов, адреса и индексы каждый раз разные.
	for i := 0; i < 3; i++ {
		lines += `{"ts":"` + day + `T10:00:0` + string(rune('0'+i)) + `Z","level":"error",` +
			`"msg":"NullReferenceException at index ` + string(rune('0'+i)) + ` addr 0xdeadbee` + string(rune('0'+i)) + `",` +
			`"stack":"LvnPlayer.Step() at /Users/x/LvnPlayer.cs:120","n":30,"dev":"устройство-A","app":"1.4.2"}` + "\n"
	}
	// Редкое, но у троих разных людей.
	for _, d := range []string{"устройство-B", "устройство-C", "устройство-D"} {
		lines += `{"ts":"` + day + `T11:00:00Z","level":"exception","msg":"Не найден ассет платья",` +
			`"stack":"ContentLoader.Fetch()","n":1,"dev":"` + d + `","app":"1.4.3"}` + "\n"
	}
	// Предупреждение — не падение.
	lines += `{"ts":"` + day + `T12:00:00Z","level":"warning","msg":"медленно","dev":"устройство-A"}` + "\n"
	if err := os.WriteFile(filepath.Join(dir, day+".jsonl"), []byte(lines), 0o644); err != nil {
		t.Fatal(err)
	}

	svc, err := NewClientLogService(dir, "t")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	svc.Routes(mux)
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/crashes?days=2", nil)
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d: %s", rec.Code, rec.Body.String())
	}
	var rep crashesReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}

	if len(rep.Groups) != 2 {
		t.Fatalf("три шумные строки — одна проблема, ожидалось две группы: %+v", rep.Groups)
	}
	// Сверху — то, что задело больше ЛЮДЕЙ, а не то, что чаще срабатывало.
	top := rep.Groups[0]
	if top.Devices != 3 {
		t.Errorf("сверху должна быть проблема трёх устройств: %+v", top)
	}
	noisy := rep.Groups[1]
	if noisy.Events != 90 {
		t.Errorf("схлопнутые повторы (n) обязаны считаться: %d вместо 90", noisy.Events)
	}
	if noisy.Devices != 1 {
		t.Errorf("шумная проблема у одного человека: %d", noisy.Devices)
	}
	// Сборка на строке — иначе «в какой версии появилось» без ответа.
	if top.Builds["1.4.3"] != 3 {
		t.Errorf("разбивка по сборкам: %+v", top.Builds)
	}
	// Образец со стеком: по нему видно, не склеилось ли лишнее.
	if noisy.SampleStack == "" {
		t.Error("образец стека потерян — чинить будет нечем")
	}
	if rep.Events != 93 {
		t.Errorf("всего падений 93 (90+3), получено %d", rep.Events)
	}
}
