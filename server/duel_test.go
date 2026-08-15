package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// Поднимает сервис дуэли на живом слушателе: долгое ожидание проверяется
// временем, а httptest.NewRecorder времени не имеет.
func duelServer(t *testing.T) (*httptest.Server, func()) {
	t.Helper()
	mux := http.NewServeMux()
	NewDuelService().Routes(mux)
	srv := httptest.NewServer(mux)
	return srv, srv.Close
}

func duelCall(t *testing.T, srv *httptest.Server, method, path, token, body string) (int, map[string]any) {
	t.Helper()
	var rd *strings.Reader
	if body == "" {
		rd = strings.NewReader("")
	} else {
		rd = strings.NewReader(body)
	}
	req, err := http.NewRequest(method, srv.URL+path, rd)
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

// Полный обмен вдвоём: комната, вход по коду, две сдачи, раскрытие.
func TestDuelRoundTrip(t *testing.T) {
	srv, done := duelServer(t)
	defer done()

	code, ta := duelOpen(t, srv)
	_, join := duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/join", "", "")
	tb, _ := join["token"].(string)
	if tb == "" {
		t.Fatal("вторая скамья не выдала токен")
	}

	if st, _ := duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", ta,
		`{"round":1,"actions":"удар,блок"}`); st != http.StatusOK {
		t.Fatalf("сдача плана: %d", st)
	}

	// ГЛАВНОЕ СВОЙСТВО: до собственной сдачи чужой ход не виден. Иначе весь
	// смысл одновременного выбора исчезает — второй просто отвечает на первого.
	_, seen := duelCall(t, srv, "GET", "/v1/duel/rooms/"+code+"?round=1", tb, "")
	if _, leaked := seen["opponent_actions"]; leaked {
		t.Fatal("чужой план виден до своей сдачи — подсмотр")
	}
	if seen["opponent"] != true {
		t.Fatal("«соперник готов» обязано быть видно: без этого не понять, кого ждём")
	}

	duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", tb, `{"round":1,"actions":"блок,удар"}`)
	_, both := duelCall(t, srv, "GET", "/v1/duel/rooms/"+code+"?round=1", tb, "")
	if both["opponent_actions"] != "удар,блок" || both["ready"] != true {
		t.Fatalf("после обоюдной сдачи ход не раскрыт: %v", both)
	}
}

// Долгое ожидание обязано просыпаться от ЧУЖОГО хода, а не по таймауту: между
// «партнёр нажал» и «у меня поехало» не должно быть интервала опроса.
func TestDuelLongPollWakesOnOpponent(t *testing.T) {
	srv, done := duelServer(t)
	defer done()

	code, ta := duelOpen(t, srv)
	_, join := duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/join", "", "")
	tb := join["token"].(string)
	duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", tb, `{"round":1,"actions":"блок"}`)

	go func() {
		time.Sleep(150 * time.Millisecond)
		duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", ta, `{"round":1,"actions":"удар"}`)
	}()

	start := time.Now()
	_, st := duelCall(t, srv, "GET", "/v1/duel/rooms/"+code+"?round=1&wait=10", tb, "")
	took := time.Since(start)

	if st["ready"] != true || st["opponent_actions"] != "удар" {
		t.Fatalf("ожидание вернулось без чужого хода: %v", st)
	}
	if took > 3*time.Second {
		t.Fatalf("ждали %v — значит досидели до таймаута, а не проснулись от хода", took)
	}
}

// План на раунд сдаётся один раз. Иначе, увидев чужой ход, можно переиграть
// свой — и одновременный выбор превращается в последовательный.
func TestDuelPlanIsFinal(t *testing.T) {
	srv, done := duelServer(t)
	defer done()
	code, ta := duelOpen(t, srv)

	duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", ta, `{"round":1,"actions":"удар"}`)
	if st, _ := duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", ta,
		`{"round":1,"actions":"пробив"}`); st != http.StatusConflict {
		t.Fatalf("переигровка хода прошла: %d", st)
	}
	// Повтор ТОГО ЖЕ плана — не переигровка, а обычный ретрай сети.
	if st, _ := duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/plan", ta,
		`{"round":1,"actions":"удар"}`); st != http.StatusOK {
		t.Fatalf("повтор того же плана отвергнут: %d", st)
	}
}

func TestDuelRejectsStrangersAndFullRooms(t *testing.T) {
	srv, done := duelServer(t)
	defer done()
	code, _ := duelOpen(t, srv)

	if st, _ := duelCall(t, srv, "GET", "/v1/duel/rooms/"+code+"?round=1", "not-a-token", ""); st != http.StatusUnauthorized {
		t.Fatalf("чужой токен пущен: %d", st)
	}
	if st, _ := duelCall(t, srv, "GET", "/v1/duel/rooms/ZZZZ?round=1", "x", ""); st != http.StatusUnauthorized {
		t.Fatalf("несуществующая комната: %d", st)
	}
	duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/join", "", "")
	if st, _ := duelCall(t, srv, "POST", "/v1/duel/rooms/"+code+"/join", "", ""); st != http.StatusConflict {
		t.Fatalf("третий сел в комнату на двоих: %d", st)
	}
}

// Код диктуют голосом, поэтому в алфавите не должно быть пар, которые слышатся
// или выглядят одинаково.
func TestDuelCodeAlphabetIsUnambiguous(t *testing.T) {
	for _, bad := range []string{"0", "O", "1", "I", "S", "5", "2", "Z", "B", "8", "G", "6", "D"} {
		if strings.Contains(duelAlphabet, bad) {
			t.Errorf("в алфавите кода спорный знак %q", bad)
		}
	}
}

func duelOpen(t *testing.T, srv *httptest.Server) (code, token string) {
	t.Helper()
	st, room := duelCall(t, srv, "POST", "/v1/duel/rooms", "", "")
	if st != http.StatusOK {
		t.Fatalf("комната не создана: %d", st)
	}
	code, _ = room["code"].(string)
	token, _ = room["token"].(string)
	if code == "" || token == "" {
		t.Fatalf("пустой код или токен: %v", room)
	}
	return code, token
}
