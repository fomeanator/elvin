package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// Права — это то, что легко сломать незаметно: любая правка обработчика может
// тихо открыть его наружу, и заметят это не тестом, а по чужим данным в чужих
// руках. Поэтому правило проверяется здесь целиком, ролями и по кругу.

func newPeople(t *testing.T) *AdminUsers {
	t.Helper()
	u, err := NewAdminUsers(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	return u
}

func TestPasswordIsNotStoredAsIs(t *testing.T) {
	u := newPeople(t)
	if err := u.SetUser("аня", "парольнадежный", RoleOwner); err != nil {
		t.Fatal(err)
	}
	rec := u.users["аня"]
	if rec.Hash == "парольнадежный" || strings.Contains(rec.Hash, "пароль") {
		t.Fatalf("пароль лежит в открытом виде: %q", rec.Hash)
	}
	if rec.Salt == "" {
		t.Fatal("без соли одинаковые пароли дают одинаковый хеш")
	}
	// Соль на каждого своя: иначе один подбор вскрывает всех сразу.
	if err := u.SetUser("петя", "парольнадежный", RoleEditor); err != nil {
		t.Fatal(err)
	}
	if u.users["петя"].Hash == rec.Hash {
		t.Fatal("одинаковые пароли дали одинаковый хеш — соль не работает")
	}
}

func TestShortPasswordRejected(t *testing.T) {
	u := newPeople(t)
	if err := u.SetUser("аня", "1234567", RoleOwner); err == nil {
		t.Fatal("семь знаков приняты")
	}
}

func TestLoginAndSession(t *testing.T) {
	u := newPeople(t)
	if err := u.SetUser("аня", "парольнадежный", RoleOwner); err != nil {
		t.Fatal(err)
	}

	if _, _, err := u.Login("аня", "неверный"); err == nil {
		t.Fatal("вошли с неверным паролем")
	}
	secret, role, err := u.Login("аня", "парольнадежный")
	if err != nil || role != RoleOwner {
		t.Fatalf("вход не удался: %v (роль %q)", err, role)
	}

	r := httptest.NewRequest("GET", "/", nil)
	r.AddCookie(&http.Cookie{Name: "lvn_admin", Value: secret})
	if s := u.Session(r); s == nil || s.Login != "аня" {
		t.Fatal("сессия не узнаётся по cookie")
	}

	// Отобранный доступ исчезает сразу, а не когда истечёт срок сессии.
	if err := u.RemoveUser("аня"); err != nil {
		t.Fatal(err)
	}
	if u.Session(r) != nil {
		t.Fatal("сессия пережила удаление учётки")
	}
}

func TestLoginIsCaseInsensitiveOnName(t *testing.T) {
	u := newPeople(t)
	_ = u.SetUser("Аня", "парольнадежный", RoleOwner)
	if _, _, err := u.Login("аня", "парольнадежный"); err != nil {
		t.Fatal("регистр имени не должен мешать входу:", err)
	}
}

func TestPeopleSurviveRestart(t *testing.T) {
	dir := t.TempDir()
	u1, _ := NewAdminUsers(dir)
	_ = u1.SetUser("аня", "парольнадежный", RoleOwner)

	u2, err := NewAdminUsers(dir)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := u2.Login("аня", "парольнадежный"); err != nil {
		t.Fatal("после перезапуска учётка потерялась:", err)
	}
}

// Собственно ворота: кого пускают на чтение, на запись и к управлению людьми.
func TestGateRoles(t *testing.T) {
	u := newPeople(t)
	_ = u.SetUser("аня", "парольнадежный", RoleOwner)
	_ = u.SetUser("петя", "парольредактор", RoleEditor)
	_ = u.SetUser("гость", "парольгостя", RoleViewer)

	old := adminPeople
	adminPeople = u
	defer func() { adminPeople = old }()

	sess := func(login, pass string) string {
		s, _, err := u.Login(login, pass)
		if err != nil {
			t.Fatal(err)
		}
		return s
	}
	secrets := map[string]string{
		"аня":   sess("аня", "парольнадежный"),
		"петя":  sess("петя", "парольредактор"),
		"гость": sess("гость", "парольгостя"),
	}

	ask := func(who, method string, need string) int {
		r := httptest.NewRequest(method, "/v1/admin/что-нибудь", nil)
		switch who {
		case "токен":
			r.Header.Set("Authorization", "Bearer ключ")
		case "никто":
		default:
			r.AddCookie(&http.Cookie{Name: "lvn_admin", Value: secrets[who]})
		}
		w := httptest.NewRecorder()
		if need == "" {
			adminAllowed(w, r, "ключ")
		} else {
			adminAllowedRole(w, r, "ключ", need)
		}
		return w.Code
	}

	cases := []struct {
		who, method, need string
		want              int
	}{
		// Чтение доступно всем, кто вообще вошёл.
		{"аня", "GET", "", 200}, {"петя", "GET", "", 200}, {"гость", "GET", "", 200},
		{"токен", "GET", "", 200},
		{"никто", "GET", "", http.StatusUnauthorized},
		// Запись — от редактора и выше.
		{"аня", "POST", "", 200}, {"петя", "POST", "", 200},
		{"гость", "POST", "", http.StatusForbidden},
		{"никто", "POST", "", http.StatusUnauthorized},
		// Управление людьми и выдача ключа — только владелец.
		{"аня", "GET", RoleOwner, 200},
		{"петя", "GET", RoleOwner, http.StatusForbidden},
		{"гость", "GET", RoleOwner, http.StatusForbidden},
	}
	for _, c := range cases {
		if got := ask(c.who, c.method, c.need); got != c.want {
			t.Errorf("%s %s (нужно %q): получили %d, ждали %d", c.who, c.method, c.need, got, c.want)
		}
	}
}

// Пока никого не завели и токена нет, админки просто НЕТ. Отвечать «введите
// пароль» там, где входа не существует, значит звать его подбирать.
func TestGateClosedWhenNothingConfigured(t *testing.T) {
	old := adminPeople
	adminPeople = newPeople(t)
	defer func() { adminPeople = old }()

	w := httptest.NewRecorder()
	adminAllowed(w, httptest.NewRequest("GET", "/v1/admin/x", nil), "")
	if w.Code != http.StatusForbidden {
		t.Fatalf("ждали 403 «админки нет», получили %d", w.Code)
	}
}

// Ключ сборки продолжает работать: им ходят публикация и выгрузка, и сломать
// их ради красоты нельзя.
func TestMachineTokenStillWorks(t *testing.T) {
	old := adminPeople
	adminPeople = newPeople(t)
	defer func() { adminPeople = old }()

	r := httptest.NewRequest("POST", "/v1/admin/x", nil)
	r.Header.Set("Authorization", "Bearer ключ")
	w := httptest.NewRecorder()
	if !adminAllowed(w, r, "ключ") {
		t.Fatalf("токен сборки отвергнут (%d)", w.Code)
	}
}
