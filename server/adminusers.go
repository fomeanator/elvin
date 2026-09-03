package main

// ПОЛЬЗОВАТЕЛИ АДМИНКИ — вместо одного общего пароля.
//
// До сих пор вход в панель был один на всех: строка-токен в конфиге сервера.
// Пока проект вёл один человек, этого хватало; как только появляются
// партнёр, редактор и подрядчик — не хватает сразу и по трём причинам.
//
// Первая: общий пароль нельзя ОТОБРАТЬ у одного, не сменив всем. Ушёл
// человек — меняй строку и рассылай новую всем остальным.
//
// Вторая: по логам не видно, КТО что сделал. «Кто-то опубликовал главу и
// снёс сцену» — при общем входе это конец расследования, а не начало.
//
// Третья: нельзя дать разные права. Редактору нужна публикация текста, но не
// нужен доступ к кошелькам и заказам; при одном пароле он получает всё.
//
// Здесь заведены именованные учётные записи с ролями и сессиями. Старый
// токен НЕ отменён намеренно: им ходят инструменты сборки и публикации, и
// ломать их ради красоты нельзя — он остаётся как «ключ машины», а люди
// заходят под своими именами.
//
// Пароли хранятся не сами, а выведенным ключом (PBKDF2-HMAC-SHA256, 200 000
// итераций, случайная соль на каждого). Библиотек для этого не подключаем:
// вывод занимает пятнадцать строк на стандартной библиотеке, а лишняя
// зависимость в публичном репозитории — это ещё один чужой код, за который
// мы отвечаем.

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// Роли. Их намеренно мало: каждая новая — это ещё одна матрица «кому что
// можно», а её приходится держать в голове при каждой правке.
const (
	RoleOwner  = "owner"  // всё, включая управление людьми
	RoleEditor = "editor" // содержание: сцены, тексты, ассеты
	RoleViewer = "viewer" // только смотреть
)

type adminUser struct {
	Login   string `json:"login"`
	Salt    string `json:"salt"`
	Hash    string `json:"hash"`
	Role    string `json:"role"`
	Created string `json:"created"`
	// Когда человек заходил в последний раз — чтобы видеть забытые учётки.
	LastSeen string `json:"last_seen,omitempty"`
}

type adminSession struct {
	Login   string
	Role    string
	Expires time.Time
}

// AdminUsers — учётные записи и живые сессии панели.
type AdminUsers struct {
	mu       sync.Mutex
	path     string
	users    map[string]*adminUser    // логин → запись
	sessions map[string]*adminSession // секрет сессии → сессия

	// Сколько живёт сессия без обращения. Сутки: панель открывают на день
	// работы, а не на неделю, и брошенная вкладка не должна оставаться
	// пропуском навсегда.
	TTL time.Duration
}

// adminUsersFile — имя файла учёток панели.
const adminUsersFile = "admin-users.json"

// adminUsersDir — где лежат учётки: в services/, рядом с остальными
// служебными данными, которые сервер никому не отдаёт.
//
// До 03.09.2026 файл лежал в корне контента и уходил игрокам как картинка —
// соли и хэши паролей всех владельцев были доступны любому по адресу
// /content/admin-users.json (аудит, проверено снаружи на проде). Переезд
// делает сервер сам при первом старте: старый файл переносится, а если новый
// уже есть — старый удаляется, потому что лежать в публичном корне ему нельзя
// ни под каким предлогом. Статика на всякий случай знает это имя и не отдаёт
// его нигде (privateRel) — второй рубеж для старого бинаря на чужом сервере.
func adminUsersDir(content string) string {
	dir := filepath.Join(content, "services")
	old := filepath.Join(content, adminUsersFile)
	now := filepath.Join(dir, adminUsersFile)
	if _, err := os.Stat(old); err != nil {
		return dir
	}
	// ПЕРЕЕЗД ЛИБО СЛУЧАЕТСЯ, ЛИБО КРИЧИТ. Молчаливый провал здесь — худший
	// исход из возможных: в панель не войдёт никто (учёток на новом месте
	// нет), а старый файл останется лежать в публичном корне — то есть
	// дыра открыта, а доступ закрыт, и оба факта никак не объявлены.
	stat, err := os.Stat(now)
	switch {
	case errors.Is(err, os.ErrNotExist):
		// Перенос — ОБЩИМ домом (builds.go): он уже переживает переезд между
		// томами, где Rename честно отказывается (EXDEV), а services/ бывает
		// отдельным монтированием. Второй такой же перенос рядом означал бы
		// два ответа на один вопрос. Каталог назначения заводим сами: у
		// сборок он всегда есть, а здесь на свежем боксе его ещё нет.
		merr := os.MkdirAll(dir, 0o755)
		if merr == nil {
			merr = moveFile(old, now)
		}
		if merr != nil {
			log.Printf("[admin] ВНИМАНИЕ: учётки НЕ переехали из корня контента (%v). "+
				"Файл остаётся публично раздаваемым по /content/%s — перенесите его "+
				"в services/ руками и перезапустите сервер", merr, adminUsersFile)
			// Возвращаем СТАРЫЙ каталог: работать с настоящими учётками
			// важнее, чем сделать вид, что переезд удался, и запереть всех.
			return content
		}
		// Файл с солями и хэшами читает только сервер: 0600, а не 0644, с
		// которыми он приехал из публичного корня.
		if cerr := os.Chmod(now, 0o600); cerr != nil {
			log.Printf("[admin] права на %s не ужались до 0600: %v", now, cerr)
		}
		log.Printf("[admin] учётки переехали из корня контента в services/: из корня они раздавались публично")
	case err != nil:
		// Новое место не прочиталось (права, битый том) — трогать старое
		// нельзя: это единственная читаемая копия.
		log.Printf("[admin] ВНИМАНИЕ: %s не прочитать (%v) — работаем со старым файлом в корне контента; "+
			"он раздаётся публично, разберитесь с правами на services/", now, err)
		return content
	case stat != nil:
		if rerr := os.Remove(old); rerr != nil {
			log.Printf("[admin] ВНИМАНИЕ: копия учёток осталась в публичном корне (%v) — удалите %s руками", rerr, old)
		} else {
			log.Printf("[admin] старая копия учёток в корне контента удалена — действующая лежит в services/")
		}
	}
	return dir
}

func NewAdminUsers(dir string) (*AdminUsers, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &AdminUsers{
		path:     filepath.Join(dir, adminUsersFile),
		users:    map[string]*adminUser{},
		sessions: map[string]*adminSession{},
		TTL:      24 * time.Hour,
	}
	raw, err := os.ReadFile(s.path)
	if err == nil {
		var list []adminUser
		if json.Unmarshal(raw, &list) == nil {
			for i := range list {
				u := list[i]
				s.users[strings.ToLower(u.Login)] = &u
			}
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	return s, nil
}

// Empty — заведён ли хоть один человек. Пока нет, панель пускает по старому
// токену: иначе первый запуск на новой машине оказался бы запертым снаружи.
func (s *AdminUsers) Empty() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.users) == 0
}

// ── хранение паролей ────────────────────────────────────────────────────────

// pbkdf2 — вывод ключа из пароля (RFC 8018). Медленность здесь и есть
// защита: подбор по украденному файлу становится дороже ровно во столько
// раз, сколько итераций.
func pbkdf2SHA256(password, salt []byte, iter, keyLen int) []byte {
	out := make([]byte, 0, keyLen)
	block := make([]byte, 4)
	for i := 1; len(out) < keyLen; i++ {
		binary.BigEndian.PutUint32(block, uint32(i))
		mac := hmac.New(sha256.New, password)
		mac.Write(salt)
		mac.Write(block)
		u := mac.Sum(nil)
		t := make([]byte, len(u))
		copy(t, u)
		for n := 1; n < iter; n++ {
			mac := hmac.New(sha256.New, password)
			mac.Write(u)
			u = mac.Sum(nil)
			for j := range t {
				t[j] ^= u[j]
			}
		}
		out = append(out, t...)
	}
	return out[:keyLen]
}

const pbkdf2Iter = 200_000

func derive(password, saltHex string) string {
	salt, _ := hex.DecodeString(saltHex)
	return hex.EncodeToString(pbkdf2SHA256([]byte(password), salt, pbkdf2Iter, 32))
}

func newSalt() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// ── учётные записи ──────────────────────────────────────────────────────────

// SetUser заводит человека или меняет ему пароль и роль.
func (s *AdminUsers) SetUser(login, password, role string) error {
	login = strings.ToLower(strings.TrimSpace(login))
	if login == "" || len(password) < 8 {
		return errors.New("нужен логин и пароль не короче восьми знаков")
	}
	switch role {
	case RoleOwner, RoleEditor, RoleViewer:
	default:
		return errors.New("роль бывает owner, editor или viewer")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	salt := newSalt()
	u := &adminUser{
		Login:   login,
		Salt:    salt,
		Hash:    derive(password, salt),
		Role:    role,
		Created: time.Now().UTC().Format(time.RFC3339),
	}
	if old, ok := s.users[login]; ok {
		u.Created = old.Created
	}
	s.users[login] = u
	return s.persistLocked()
}

// RemoveUser убирает учётку и разом гасит её сессии: отобранный доступ
// должен исчезать сразу, а не когда истечёт срок.
func (s *AdminUsers) RemoveUser(login string) error {
	login = strings.ToLower(strings.TrimSpace(login))
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.users[login]; !ok {
		return errors.New("нет такого пользователя")
	}
	delete(s.users, login)
	for secret, sess := range s.sessions {
		if sess.Login == login {
			delete(s.sessions, secret)
		}
	}
	return s.persistLocked()
}

func (s *AdminUsers) persistLocked() error {
	list := make([]adminUser, 0, len(s.users))
	for _, u := range s.users {
		list = append(list, *u)
	}
	raw, err := json.MarshalIndent(list, "", "  ")
	if err != nil {
		return err
	}
	// Файл с хешами читаем только мы: 0600, как и всё, что похоже на ключи.
	//
	// ЧЕРЕЗ ДОМ АТОМАРНОЙ ЗАПИСИ, а не напрямую. Прямая запись усекает файл
	// ДО того, как напишет новое: падение, полный диск или выключение питания
	// посреди неё оставляют список администраторов пустым или обрезанным — то
	// есть отбирают доступ к панели у всех сразу, и восстановить его можно
	// только руками на сервере. Дом пишет во временный файл рядом, синхронизирует
	// и переименовывает: читатель видит либо старый список целиком, либо новый.
	return atomicWrite(s.path, raw, 0o600)
}

// ── вход ────────────────────────────────────────────────────────────────────

// Login сверяет пароль и открывает сессию. Возвращает секрет для cookie.
func (s *AdminUsers) Login(login, password string) (secret, role string, err error) {
	login = strings.ToLower(strings.TrimSpace(login))
	s.mu.Lock()
	defer s.mu.Unlock()
	u, ok := s.users[login]
	if !ok {
		// Сравниваем всё равно: иначе по времени ответа видно, какие логины
		// существуют, а какие нет.
		derive(password, newSalt())
		return "", "", errors.New("неверный логин или пароль")
	}
	want, _ := hex.DecodeString(u.Hash)
	got, _ := hex.DecodeString(derive(password, u.Salt))
	if subtle.ConstantTimeCompare(want, got) != 1 {
		return "", "", errors.New("неверный логин или пароль")
	}
	b := make([]byte, 32)
	_, _ = rand.Read(b)
	secret = hex.EncodeToString(b)
	s.sessions[secret] = &adminSession{Login: u.Login, Role: u.Role, Expires: time.Now().Add(s.TTL)}
	u.LastSeen = time.Now().UTC().Format(time.RFC3339)
	_ = s.persistLocked()
	return secret, u.Role, nil
}

// Session возвращает живую сессию запроса и продлевает её.
func (s *AdminUsers) Session(r *http.Request) *adminSession {
	secret := sessionSecret(r)
	if secret == "" {
		return nil
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	sess, ok := s.sessions[secret]
	if !ok {
		return nil
	}
	if time.Now().After(sess.Expires) {
		delete(s.sessions, secret)
		return nil
	}
	sess.Expires = time.Now().Add(s.TTL)
	return sess
}

func (s *AdminUsers) Logout(r *http.Request) {
	secret := sessionSecret(r)
	if secret == "" {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.sessions, secret)
}

// sessionSecret достаёт секрет из cookie или заголовка: браузер шлёт первое,
// инструменты — второе.
func sessionSecret(r *http.Request) string {
	if c, err := r.Cookie("lvn_admin"); err == nil && c.Value != "" {
		return c.Value
	}
	if v := r.Header.Get("X-Lvn-Session"); v != "" {
		return v
	}
	return ""
}

// List отдаёт учётки без хешей — для экрана управления людьми.
func (s *AdminUsers) List() []adminUser {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]adminUser, 0, len(s.users))
	for _, u := range s.users {
		c := *u
		c.Hash, c.Salt = "", ""
		out = append(out, c)
	}
	return out
}
