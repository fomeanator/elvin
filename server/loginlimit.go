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
	"runtime"
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
	// Потолок числа запомненных источников — чтобы перебор с тысяч адресов
	// не превращался в рост памяти.
	loginKeysMax = 10000
)

// СЧЁТ ПО АДРЕСУ НЕ ЗАЩИЩАЕТ ЯДРО, и это надо сказать вслух.
//
// Ограничение по источнику останавливает перебор с одного адреса — и только
// его. У атакующего с ботнетом или с одной подсетью IPv6 адресов столько,
// сколько нужно, и каждый честно получает свои десять попыток по десятой
// секунды процессора. На боксе с ОДНИМ ядром этого хватает, чтобы игра
// перестала отвечать: проверка пароля съест его целиком, а виноватым будет
// выглядеть сервер.
//
// Поэтому ограничивается не число личностей, а САМА ДОРОГАЯ РАБОТА: сколько
// проверок пароля идёт одновременно. Половина ядер, но не меньше одного:
// вошедшему ждать нечего, а перебору очередь ставит потолок, не зависящий от
// числа адресов. Не дождался за loginWaitBudget — получает отказ, а не место
// в очереди: очередь без потолка это та же занятая память.
var loginWork = make(chan struct{}, loginWorkers())

func loginWorkers() int {
	if n := runtime.NumCPU() / 2; n > 0 {
		return n
	}
	return 1
}

// loginWaitBudget — сколько ждать своей очереди на проверку пароля.
const loginWaitBudget = 2 * time.Second

// takeLoginSlot занимает место в очереди проверок; false — не дождались.
func takeLoginSlot() bool {
	select {
	case loginWork <- struct{}{}:
		return true
	case <-time.After(loginWaitBudget):
		return false
	}
}

func freeLoginSlot() { <-loginWork }

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
	// ПАМЯТЬ ОГРАНИЧЕНА ПО-НАСТОЯЩЕМУ. Уборки протухшего мало: при переборе с
	// тысяч СВЕЖИХ адресов выбрасывать нечего, и карта росла бы дальше. Если
	// после уборки предел всё равно превышен — карта сбрасывается целиком.
	// Это амнистия всем накопленным промахам, и она честнее выбора «кого
	// забыть»: сама ситуация означает распределённую атаку, против которой
	// работает не счёт адресов, а очередь проверок (loginWork).
	if len(l.fails) > loginKeysMax {
		for k := range l.fails {
			l.prune(k, now)
		}
		if len(l.fails) > loginKeysMax {
			l.fails = map[string][]time.Time{}
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
	ip := net.ParseIP(host)
	if ip != nil && ip.IsLoopback() {
		// ЗНАЧЕНИЕ ПРОВЕРЯЕТСЯ. Заголовок ставит nginx, но в петлю стучится и
		// всякий локальный процесс: непроверенная строка стала бы ключом, по
		// которому можно и обойти счёт, и запереть чужой адрес.
		if real := net.ParseIP(strings.TrimSpace(r.Header.Get("X-Real-IP"))); real != nil {
			return loginKeyOf(real)
		}
	}
	if ip != nil {
		return loginKeyOf(ip)
	}
	return host
}

// loginKeyOf — под каким ключом считать промахи. У IPv4 это сам адрес, у
// IPv6 — подсеть /64: провайдер выдаёт её одному абоненту целиком, и счёт по
// полному адресу означал бы бесконечный запас бесплатных попыток на человека.
func loginKeyOf(ip net.IP) string {
	if ip.To4() != nil {
		return ip.String()
	}
	return ip.Mask(net.CIDRMask(64, 128)).String() + "/64"
}
