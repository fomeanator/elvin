package main

// Откуда пришёл игрок: канал привлечения в его профиле.
//
// Рекламу без этого запускать нельзя. Деньги уходят, игроки приходят, а какой
// креатив привёл платящего — неизвестно; все отчёты умеют сказать «сколько», и
// ни один не умеет сказать «за чей счёт».
//
// Два решения, которые определяют всё остальное:
//
// ПЕРВОЕ КАСАНИЕ, НЕИЗМЕНЯЕМО. Канал записывается один раз и больше никогда не
// переписывается. Игрок, пришедший по рекламе в марте и переустановивший игру
// в мае по прямой ссылке, привлечён рекламой — переписав канал, мы бы задним
// числом обнулили результат кампании, которая сработала. Последнее касание —
// отдельная метрика, и заводить её надо отдельным полем, а не порчей этого.
//
// РАЗБИРАЕТ СЕРВЕР. Клиент присылает сырую строку (адрес диплинка или метку
// установки) и ничего не разбирает. Разбор в одном месте — это одна реализация
// вместо трёх (Android, iOS, веб), возможность починить его без новой сборки и
// сохранённый оригинал: то, чего мы сегодня не поняли, завтра ещё можно
// прочитать. Обратное — разбирать на клиенте — означает, что ошибка разбора
// становится вечной, потому что переписать уже отправленное нельзя.

import (
	"encoding/json"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// playerAttribution — канал привлечения. Пустые поля означают «не назвали»,
// а не «прямой переход»: отличать одно от другого важно, иначе органика и
// сломанная разметка сливаются в одну кучу.
type playerAttribution struct {
	Source   string `json:"source,omitempty"`   // utm_source: где увидел (telegram, vk)
	Medium   string `json:"medium,omitempty"`   // utm_medium: какого рода касание (cpc, post)
	Campaign string `json:"campaign,omitempty"` // utm_campaign: какая кампания
	Content  string `json:"content,omitempty"`  // utm_content: какой креатив
	Term     string `json:"term,omitempty"`     // utm_term: ключевое слово
	Ref      string `json:"ref,omitempty"`      // произвольная метка ?ref= — для ссылок вручную
	// Raw — исходная строка целиком. Разметку пишут люди, и половина ссылок
	// приходит с опечатками и своими параметрами; сохранённый оригинал — это
	// единственный способ разобраться потом.
	Raw string `json:"raw,omitempty"`
	At  string `json:"at,omitempty"` // когда записали первое касание
}

// empty — «меток не было вовсе». Строка, из которой не вышло ни одного поля,
// не должна занимать место первого касания: иначе первый же запуск без ссылки
// навсегда закроет игроку возможность быть атрибутированным.
func (a playerAttribution) empty() bool {
	return a.Source == "" && a.Medium == "" && a.Campaign == "" &&
		a.Content == "" && a.Term == "" && a.Ref == ""
}

// Channel — как канал называется в отчётах. Кампания важнее источника:
// вопрос «что окупилось» задают про кампанию, а источник — это её свойство.
func (a playerAttribution) Channel() string {
	switch {
	case a.Campaign != "":
		if a.Source != "" {
			return a.Source + "/" + a.Campaign
		}
		return a.Campaign
	case a.Source != "":
		return a.Source
	case a.Ref != "":
		return a.Ref
	}
	return ""
}

// parseAttribution разбирает адрес диплинка или строку меток установки.
//
// Принимает и полный адрес (https://…?utm_source=tg), и голую строку
// параметров (utm_source=tg&utm_campaign=aug) — Play Install Referrer отдаёт
// именно вторую, и требовать от клиента приводить одно к другому значит
// перекладывать на него ровно тот разбор, который мы решили не отдавать.
func parseAttribution(raw string) playerAttribution {
	a := playerAttribution{Raw: clip(strings.TrimSpace(raw), 512)}
	if a.Raw == "" {
		return a
	}
	q := a.Raw
	if i := strings.IndexByte(q, '?'); i >= 0 {
		q = q[i+1:]
	}
	if i := strings.IndexByte(q, '#'); i >= 0 {
		q = q[:i]
	}
	vals, err := url.ParseQuery(q)
	if err != nil {
		// Битую строку не выбрасываем: Raw уже сохранён, и по нему потом
		// видно, что именно прислали. Молча потерять её — значит потерять
		// единственный след кампании.
		return a
	}
	get := func(keys ...string) string {
		for _, k := range keys {
			if v := strings.TrimSpace(vals.Get(k)); v != "" {
				return clip(v, 64)
			}
		}
		return ""
	}
	a.Source = get("utm_source", "source", "src")
	a.Medium = get("utm_medium", "medium")
	a.Campaign = get("utm_campaign", "campaign")
	a.Content = get("utm_content", "content")
	a.Term = get("utm_term", "term")
	a.Ref = get("ref", "referrer_id")
	return a
}

// SetAttributionFirstTouch записывает канал, если его ещё нет. Возвращает
// сохранённое значение и признак «записали именно сейчас».
func (s *AuthService) SetAttributionFirstTouch(userID string, a playerAttribution) (playerAttribution, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	u := s.users[userID]
	if u == nil {
		return playerAttribution{}, false
	}
	if u.Attr != nil && !u.Attr.empty() {
		return *u.Attr, false // первое касание уже занято — не трогаем
	}
	if a.empty() {
		// Запуск без меток не занимает место: игрок мог зайти напрямую
		// сегодня и прийти по рекламе завтра.
		if u.Attr != nil {
			return *u.Attr, false
		}
		return playerAttribution{}, false
	}
	a.At = time.Now().UTC().Format(time.RFC3339)
	u.Attr = &a
	if err := s.saveUserLocked(userID); err != nil {
		return a, false
	}
	return a, true
}

// AttributionOf — канал игрока, для отчётов.
func (s *AuthService) AttributionOf(userID string) playerAttribution {
	s.mu.Lock()
	defer s.mu.Unlock()
	if u := s.users[userID]; u != nil && u.Attr != nil {
		return *u.Attr
	}
	return playerAttribution{}
}

// Channels — канал каждого игрока разом: отчёту нужна вся карта, а не
// тысяча отдельных запросов под замком.
func (s *AuthService) Channels() map[string]string {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make(map[string]string, len(s.users))
	for id, u := range s.users {
		if u.Attr != nil {
			if ch := u.Attr.Channel(); ch != "" {
				out[id] = ch
			}
		}
	}
	return out
}

// POST /v1/attribution {"raw": "https://…?utm_campaign=test"} — клиент шлёт
// это один раз при первом запуске. Повтор безопасен: первое касание не
// переписывается, поэтому потерянный ответ можно спокойно переслать.
func (s *AuthService) handleAttribution(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	userID := s.UserFromRequest(r)
	if !requireUser(w, userID) {
		return
	}
	var body struct {
		Raw string `json:"raw"`
	}
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyTiny)).Decode(&body) != nil {
		http.Error(w, `{"raw": "<адрес диплинка или строка меток>"} required`, http.StatusBadRequest)
		return
	}
	attr, wrote := s.SetAttributionFirstTouch(userID, parseAttribution(body.Raw))
	writeJSON(w, http.StatusOK, map[string]any{
		"attribution": attr, "channel": attr.Channel(), "first_touch": wrote,
	})
}
