package main

// СЧЁТ НЕУДАЧНЫХ ВХОДОВ В ПАНЕЛЬ — подбор пароля упирается в паузу, а не в
// ядро прод-бокса.
//
// Пароли хранятся PBKDF2 на 200 000 итераций: проверка стоит десятую секунды
// процессора. Это защищает хэши В ФАЙЛЕ, но не сервер: без счётчика один
// клиент занимал бы единственное ядро прод-бокса целиком и перебирал бы
// пароли со скоростью десяти в секунду — и ни одна запись в журнале этого не
// отличала бы от забывчивого редактора. nginx перед сервером режет /v1/admin
// до пяти запросов в секунду, но сервер обязан держать себя сам: стенды и
// вторая студия стоят без nginx.

import (
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

// loginMaxFails промахов за loginWindow — и источник ждёт, пока окно не
// уедет. Десять за десять минут: забывчивому человеку хватает с запасом,
// перебору — нет.
const (
	loginMaxFails = 10
	loginWindow   = 10 * time.Minute
)

// failLimiter помнит моменты промахов по источнику. Nil-безопасен: служба,
// собранная без него (тесты, урезанная сборка), просто не ограничивает.
type failLimiter struct {
	mu     sync.Mutex
	fails  map[string][]time.Time
	max    int
	window time.Duration
	now    func() time.Time
}

func newFailLimiter(max int, window time.Duration) *failLimiter {
	return &failLimiter{fails: map[string][]time.Time{}, max: max, window: window, now: time.Now}
}

// wait — сколько источнику ждать до следующей попытки; ноль — можно.
func (l *failLimiter) wait(key string) time.Duration {
	if l == nil {
		return 0
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	kept := l.prune(key, now)
	if len(kept) < l.max {
		return 0
	}
	return kept[0].Add(l.window).Sub(now)
}

// fail — ещё один промах от источника.
func (l *failLimiter) fail(key string) {
	if l == nil {
		return
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.now()
	l.fails[key] = append(l.prune(key, now), now)
	// Память ограничена: перебор с тысяч адресов не должен раздувать карту.
	// Уборка редкая и грубая — выбросить всё протухшее у всех.
	if len(l.fails) > 10000 {
		for k := range l.fails {
			l.prune(k, now)
		}
	}
}

// clear — источник вошёл: его промахи прощены.
func (l *failLimiter) clear(key string) {
	if l == nil {
		return
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	delete(l.fails, key)
}

// prune выбрасывает промахи старше окна; пустой источник уходит из карты.
// Звать под замком.
func (l *failLimiter) prune(key string, now time.Time) []time.Time {
	all := l.fails[key]
	kept := all[:0]
	for _, t := range all {
		if now.Sub(t) < l.window {
			kept = append(kept, t)
		}
	}
	if len(kept) == 0 {
		delete(l.fails, key)
		return nil
	}
	l.fails[key] = kept
	return kept
}

// loginPeer — источник попытки входа.
//
// За nginx все запросы приходят с 127.0.0.1, и считать по адресу сокета
// значило бы запереть всех после десяти чужих промахов. Заголовок X-Real-IP
// ставит сам nginx (см. шаблон в deploy/), и верим ему ТОЛЬКО когда сокет —
// петля: снаружи его напишет кто угодно. Аналитика поступает нарочно иначе
// (clientIP игнорирует заголовки): там подделка плодила бы ведёрки без счёта,
// а здесь она, наоборот, вытащила бы атакующего из общего ведра.
func loginPeer(r *http.Request) string {
	host := clientIP(r)
	if ip := net.ParseIP(host); ip != nil && ip.IsLoopback() {
		if real := strings.TrimSpace(r.Header.Get("X-Real-IP")); real != "" {
			return real
		}
	}
	return host
}
