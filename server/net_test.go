package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// Живой слушатель, а не Recorder: долгое ожидание проверяется временем.
func netServer(t *testing.T) (*httptest.Server, func()) {
	t.Helper()
	mux := http.NewServeMux()
	NewNetService().Routes(mux)
	srv := httptest.NewServer(mux)
	return srv, srv.Close
}

func netCall(t *testing.T, srv *httptest.Server, method, path, token, body string) (int, map[string]any) {
	t.Helper()
	req, err := http.NewRequest(method, srv.URL+path, strings.NewReader(body))
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("do: %v", err)
	}
	defer resp.Body.Close()
	var out map[string]any
	json.NewDecoder(resp.Body).Decode(&out)
	return resp.StatusCode, out
}

func netOpen(t *testing.T, srv *httptest.Server) (code, token string) {
	t.Helper()
	st, room := netCall(t, srv, "POST", "/v1/net/rooms", "", "")
	if st != http.StatusOK {
		t.Fatalf("комната не открылась: %d", st)
	}
	code, _ = room["code"].(string)
	token, _ = room["token"].(string)
	if code == "" || token == "" {
		t.Fatalf("пустой код или токен: %v", room)
	}
	return code, token
}

func netSit(t *testing.T, srv *httptest.Server, code string) string {
	t.Helper()
	st, join := netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/join", "", "")
	if st != http.StatusOK {
		t.Fatalf("не сел за стол: %d", st)
	}
	return join["token"].(string)
}

// ОДНОВРЕМЕННЫЙ ВЫБОР — то, на чём держится дуэль. Ящик не открывается, пока не
// положили все; открывшись, показывает чужое.
func TestNetRevealAllWithholdsUntilEveryoneHasPlaced(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)
	tb := netSit(t, srv, code)

	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/обмен:1", ta, `{"value":"удар,блок"}`)

	// Б ещё не клал — не должен видеть ничего, кроме «положил один».
	_, peek := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/обмен:1", tb, "")
	if peek["open"] != false {
		t.Fatal("ящик открылся до того, как положили все")
	}
	if _, leaked := peek["others"]; leaked {
		t.Fatal("чужое видно до своего хода — подсмотр")
	}
	if peek["placed"] != float64(1) {
		t.Fatalf("должно быть видно, что кто-то уже положил: %v", peek)
	}

	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/обмен:1", tb, `{"value":"блок,удар"}`)
	_, open := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/обмен:1", tb, "")
	if open["open"] != true {
		t.Fatalf("ящик не открылся после общей сдачи: %v", open)
	}
	others := open["others"].(map[string]any)
	if others["a"] != "удар,блок" {
		t.Fatalf("чужое значение не то: %v", others)
	}
	if _, self := others["b"]; self {
		t.Fatal("в «чужом» лежит своё же")
	}
}

// ХОД ПО ОЧЕРЕДИ — шахматы, карты: положил и сразу видно.
func TestNetRevealNowIsVisibleImmediately(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)
	tb := netSit(t, srv, code)

	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/ход:7", ta, `{"value":"e2e4","reveal":"now"}`)
	_, seen := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/ход:7", tb, "")
	if seen["open"] != true {
		t.Fatalf("ход по очереди обязан быть виден сразу: %v", seen)
	}
	if seen["others"].(map[string]any)["a"] != "e2e4" {
		t.Fatalf("ход не пришёл: %v", seen)
	}
}

// Правило раскрытия задаёт первый положивший, и переключить его нельзя: иначе
// второй объявил бы ящик «видно сразу» и подсмотрел.
func TestNetRevealPolicyIsSetOnceByFirstWriter(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)
	tb := netSit(t, srv, code)

	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/k", ta, `{"value":"тайна"}`) // all по умолчанию
	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/k", tb, `{"value":"своё","reveal":"now"}`)
	_, view := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/k", tb, "")
	if view["reveal"] != revealAll {
		t.Fatalf("правило раскрытия переписали: %v", view)
	}
}

// ГОНКА: кто нажал раньше. Это единственное, чего клиент не выведет из своей
// копии игры, — поэтому порядок обязан приходить с сервера.
func TestNetRecordsArrivalOrder(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)
	tb := netSit(t, srv, code)

	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/кнопка", tb, `{"value":"жму","reveal":"now"}`)
	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/кнопка", ta, `{"value":"жму"}`)
	_, view := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/кнопка", ta, "")

	order := view["order"].([]any)
	if len(order) != 2 || order[0] != "b" {
		t.Fatalf("порядок нажатий не сохранён: %v", order)
	}
}

// Долгое ожидание обязано просыпаться от ЧУЖОГО хода, а не по таймауту.
func TestNetLongPollWakesOnPlacement(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)
	tb := netSit(t, srv, code)
	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/x", tb, `{"value":"мой"}`)

	go func() {
		time.Sleep(150 * time.Millisecond)
		netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/x", ta, `{"value":"его"}`)
	}()

	start := time.Now()
	_, view := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/x?wait=10", tb, "")
	took := time.Since(start)

	if view["open"] != true {
		t.Fatalf("ожидание вернулось закрытым: %v", view)
	}
	if took > 3*time.Second {
		t.Fatalf("ждали %v — досидели до таймаута, а не проснулись от хода", took)
	}
}

// Ожидание второго игрока: комната отвечает, как только набралось нужное число.
func TestNetWaitsForSeats(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)

	go func() {
		time.Sleep(150 * time.Millisecond)
		netSit(t, srv, code)
	}()
	start := time.Now()
	_, who := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"?need=2&wait=10", ta, "")
	if who["seats"] != float64(2) {
		t.Fatalf("не дождались второго: %v", who)
	}
	if time.Since(start) > 3*time.Second {
		t.Fatal("ожидание мест досидело до таймаута")
	}
}

// Положенное не переигрывается. Повтор того же — обычный ретрай сети.
func TestNetValueIsFinal(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, ta := netOpen(t, srv)

	netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/k", ta, `{"value":"раз"}`)
	if st, _ := netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/k", ta, `{"value":"два"}`); st != http.StatusConflict {
		t.Fatalf("значение переписали: %d", st)
	}
	if st, _ := netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/cells/k", ta, `{"value":"раз"}`); st != http.StatusOK {
		t.Fatalf("повтор того же отвергнут: %d", st)
	}
}

func TestNetRejectsStrangers(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	code, _ := netOpen(t, srv)

	if st, _ := netCall(t, srv, "GET", "/v1/net/rooms/"+code+"/cells/k", "not-a-token", ""); st != http.StatusUnauthorized {
		t.Fatalf("чужой токен пущен: %d", st)
	}
	if st, _ := netCall(t, srv, "GET", "/v1/net/rooms/ZZZZ/cells/k", "x", ""); st != http.StatusUnauthorized {
		t.Fatalf("несуществующая комната: %d", st)
	}
}

// Код диктуют голосом, поэтому в алфавите не должно быть знаков, которые
// слышатся или выглядят одинаково.
func TestNetCodeAlphabetIsUnambiguous(t *testing.T) {
	for _, bad := range []string{"0", "O", "1", "I", "S", "5", "2", "Z", "B", "8", "G", "6", "D"} {
		if strings.Contains(netAlphabet, bad) {
			t.Errorf("в алфавите кода спорный знак %q", bad)
		}
	}
}

// Зерно случайности выдаётся комнатой и ОДНО на всех, кто в ней сидит. На нём
// держится приём из сетевых игр девяностых: не пересылать случайные числа, а
// договориться о зерне — дальше все тянут одни и те же числа сами.
func TestNetRoomHandsOutOneSeedToEveryone(t *testing.T) {
	srv, done := netServer(t)
	defer done()

	st, room := netCall(t, srv, "POST", "/v1/net/rooms", "", "")
	if st != http.StatusOK {
		t.Fatalf("комната не открылась: %d", st)
	}
	seed, ok := room["seed"].(float64)
	if !ok || seed == 0 {
		t.Fatalf("комната не выдала зерно: %v", room)
	}
	code := room["code"].(string)

	_, join := netCall(t, srv, "POST", "/v1/net/rooms/"+code+"/join", "", "")
	if join["seed"] != seed {
		t.Fatalf("вошедший получил ДРУГОЕ зерно: %v против %v", join["seed"], seed)
	}
}

// Две комнаты — разные зёрна. Иначе, открыв свою, можно было бы предсказать
// чужие броски.
func TestNetSeedsDifferPerRoom(t *testing.T) {
	srv, done := netServer(t)
	defer done()
	_, a := netCall(t, srv, "POST", "/v1/net/rooms", "", "")
	_, b := netCall(t, srv, "POST", "/v1/net/rooms", "", "")
	if a["seed"] == b["seed"] {
		t.Fatalf("две комнаты получили одно зерно: %v", a["seed"])
	}
}
