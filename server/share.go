package main

import (
	"crypto/rand"
	"database/sql"
	"encoding/json"
	"fmt"
	"log"
	"math/big"
	"net/http"
	"strings"
	"time"
)

// ПЕРЕДАЧА ПРОХОЖДЕНИЯ (TR-17) — игрок отдаёт свой снимок другому человеку.
//
// Идея Ильи: «поделиться сохранением, чтобы другой доиграл». Ближайший аналог
// не в новеллах, а в книгах-играх со state в ссылке и в AI Dungeon, где чужое
// приключение продолжают с той же точки; у коммерческих новелл этого нет почти
// ни у кого.
//
// СНИМОК ПРИСЫЛАЕТ САМ ИГРОК, а не сервер читает его сейв. Разница не
// техническая: читать чужое состояние по чужому идентификатору — это дверь,
// которую пришлось бы охранять вечно, а отдать СВОЁ может только тот, у кого
// оно на руках. Заодно снимок получается ровно тем, что игрок видел на экране,
// а не тем, что успело доехать до облака.
type ShareService struct {
	db   *sql.DB
	auth *AuthService
	now  func() time.Time
}

// Сколько живёт ссылка. Месяц: этого хватает и чату, и посту блогера, а вечное
// хранение чужих снимков — это склад, который никто не разбирает.
const shareLifetime = 30 * 24 * time.Hour

// Сколько ссылок игрок может держать живыми. Не защита от злодея (он заведёт
// вторую учётку), а предел, за которым «поделиться» превращается в хостинг.
const shareLimit = 20

// Размер снимка: сейв новеллы — это переменные и позиция, десятки килобайт.
// Четверть мегабайта берём с большим запасом.
const shareMaxBody = 256 * 1024

func NewShareService(db *sql.DB, auth *AuthService) *ShareService {
	return &ShareService{db: db, auth: auth, now: time.Now}
}

func (s *ShareService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/share", s.handleCreate)
	mux.HandleFunc("/v1/share/", s.handleTake)
}

// СВОДКА ПО ССЫЛКАМ (TR-18) — сколько прохождений раздали и сколько раз их
// открыли. Это канал привлечения: блогерка кидает ссылку, по ней приходят, и
// без счёта разговор о «работает ли это» превращается в мнения.
//
// Покупки по таким ссылкам считать ЗДЕСЬ не нужно: они лежат в журнале
// кошелька причиной "share_look", и второй счёт того же события разошёлся бы
// с первым на первой же правке.
func (s *ShareService) AdminSummary() map[string]any {
	out := map[string]any{"links": 0, "opens": 0}
	var links, opens, withOpens int
	err := s.db.QueryRow(
		`SELECT count(*), coalesce(sum(taken),0), coalesce(sum(taken > 0),0)
		   FROM shares WHERE until >= ?`, s.now().UTC().Unix()).
		Scan(&links, &opens, &withOpens)
	if err != nil {
		log.Printf("share: summary: %v", err)
		return out
	}
	out["links"] = links
	out["opens"] = opens
	out["opened_links"] = withOpens
	return out
}

// КОД ССЫЛКИ — ВОСЕМЬ ЗНАКОВ БЕЗ ПОХОЖИХ. Его читают с чужого экрана и
// набирают руками, поэтому из алфавита убраны 0/O, 1/I/l: «код не работает» от
// перепутанной буквы выглядит как сломанная игра.
const shareAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"

func shareCode() (string, error) {
	var b strings.Builder
	for i := 0; i < 8; i++ {
		n, err := rand.Int(rand.Reader, big.NewInt(int64(len(shareAlphabet))))
		if err != nil {
			return "", err
		}
		b.WriteByte(shareAlphabet[n.Int64()])
	}
	return b.String(), nil
}

func (s *ShareService) handleCreate(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	userID := s.auth.UserFromRequest(r)
	if userID == "" || !reUserFile.MatchString(userID) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var req struct {
		Title string          `json:"title"`
		Body  json.RawMessage `json:"body"`
		Note  string          `json:"note"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, shareMaxBody)).Decode(&req); err != nil {
		http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
		return
	}
	if len(req.Body) == 0 || !json.Valid(req.Body) {
		http.Error(w, "body (JSON snapshot) required", http.StatusBadRequest)
		return
	}
	if len(req.Note) > 200 {
		req.Note = req.Note[:200]
	}
	s.sweep()

	var live int
	if err := s.db.QueryRow(`SELECT count(*) FROM shares WHERE owner = ?`, userID).Scan(&live); err != nil {
		log.Printf("share: count %s: %v", userID, err)
		http.Error(w, "share unavailable", http.StatusInternalServerError)
		return
	}
	if live >= shareLimit {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "too_many_shares", "limit": shareLimit})
		return
	}

	code, err := shareCode()
	if err != nil {
		http.Error(w, "code", http.StatusInternalServerError)
		return
	}
	now := s.now().UTC()
	_, err = s.db.Exec(
		`INSERT INTO shares (code, owner, title, note, body, made, until) VALUES (?,?,?,?,?,?,?)`,
		code, userID, req.Title, req.Note, string(req.Body),
		now.Unix(), now.Add(shareLifetime).Unix())
	if err != nil {
		log.Printf("share: insert %s: %v", userID, err)
		http.Error(w, "share unavailable", http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"code":  code,
		"until": now.Add(shareLifetime).Format(time.RFC3339),
	})
}

// ПОЛУЧАТЕЛЮ УЧЁТКА НЕ НУЖНА: ссылку открывают из чата, и требовать вход до
// того, как человек увидел, что ему прислали, — верный способ его потерять.
// Код сам по себе секрет: восемь знаков из тридцати одного.
func (s *ShareService) handleTake(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodGet) {
		return
	}
	code := strings.TrimPrefix(r.URL.Path, "/v1/share/")
	if code == "" || len(code) > 16 || strings.ContainsAny(code, "/?&") {
		http.Error(w, "code required", http.StatusBadRequest)
		return
	}
	code = strings.ToUpper(code)
	var title, note, body string
	var made, until int64
	err := s.db.QueryRow(
		`SELECT title, note, body, made, until FROM shares WHERE code = ?`, code).
		Scan(&title, &note, &body, &made, &until)
	if err == sql.ErrNoRows || (err == nil && until < s.now().UTC().Unix()) {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "share_not_found"})
		return
	}
	if err != nil {
		log.Printf("share: get %s: %v", code, err)
		http.Error(w, "share unavailable", http.StatusInternalServerError)
		return
	}
	// Счётчик открытий: по нему автор ссылки увидит, что его прохождение
	// смотрят, а мы — что фича живая.
	if _, uerr := s.db.Exec(`UPDATE shares SET taken = taken + 1 WHERE code = ?`, code); uerr != nil {
		log.Printf("share: bump %s: %v", code, uerr)
	}
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"code":%q,"title":%q,"note":%q,"made":%d,"body":%s}`,
		code, title, note, made, body)
}

// Протухшее убираем при каждой выдаче кода: отдельного уборщика заводить ради
// таблицы в сотни строк незачем, а вечный склад чужих снимков — заводить тем
// более.
func (s *ShareService) sweep() {
	if _, err := s.db.Exec(`DELETE FROM shares WHERE until < ?`, s.now().UTC().Unix()); err != nil {
		log.Printf("share: sweep: %v", err)
	}
}
