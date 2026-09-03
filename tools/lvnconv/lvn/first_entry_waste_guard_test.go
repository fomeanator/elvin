package lvn

import (
	"os"
	"strings"
	"testing"
)

// ПЕРВЫЙ ВХОД НЕ ГРЕЕТ ТО, ЧЕГО НЕ ПОКАЖЕТ.
//
// Живой трейс первого входа (04.09, учётка и кэш снесены руками) показал три
// работы, которых новый игрок не заказывал. Он уходит в воронку — глава ноль
// вместо витрины, — а запуск в это время:
//
//   - грел ПОЛОТНО ВИТРИНЫ (2000×1500, 3,4 МБ, 99 мс распаковки) и четыре
//     слоя куклы героини: ворота вводной спрашивались на четыреста
//     миллисекунд позже самого прогрева;
//   - запрашивал СКРИПТЫ ВСЕХ ГЛАВ ВСЕХ новелл — тридцать шесть штук разом, и
//     единственный нужный ждал среди них 1547 мс;
//   - тянул обложки и фоны чужих новелл, которых игрок не увидит и через
//     главу.
//
// Всё это не ошибки по отдельности: каждая работа осмысленна для того, кто
// идёт на витрину. Беда в том, что вопрос «а он туда идёт?» задавался ПОЗЖЕ.
// Дефект тихий и не выражается тестом поведения — обе стороны по отдельности
// правы, — поэтому страж смотрит в исходник.
func TestFirstEntryDoesNotWarmTheShowcase(t *testing.T) {
	const app = "../../../unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.cs"
	raw, err := os.ReadFile(app)
	if err != nil {
		t.Skipf("нет %s: %v", app, err)
	}
	src := string(raw)

	at := strings.Index(src, "WarmMenuCanvas()")
	if at < 0 {
		t.Fatal("прогрев витрины не найден — страж смотрит не туда, поправьте его")
	}
	// Условие стоит на той же строке или на предыдущей: прогрев обязан
	// зависеть от ответа ворот, а не случаться всегда.
	lineStart := strings.LastIndex(src[:at], "\n") + 1
	prevStart := strings.LastIndex(src[:lineStart-1], "\n") + 1
	near := src[prevStart : at+len("WarmMenuCanvas()")]
	// Ищем «if (», а не «if»: подстрока «if» живёт внутри слова manifest,
	// которое стоит ровно в этой же строке — и страж молча проходил.
	if !strings.Contains(near, "if (") {
		t.Errorf("витрина греется БЕЗУСЛОВНО: %q\n"+
			"новый игрок уходит в воронку, и полотно с куклой займут полосу сети и "+
			"распаковки ровно тогда, когда их ждут критичные ассеты главы", strings.TrimSpace(near))
	}
	if !strings.Contains(near, "LvnIntro.Pending") {
		t.Errorf("прогрев витрины не спрашивает ворота вводной: %q", strings.TrimSpace(near))
	}
}

// СКРИПТЫ ЧУЖИХ НОВЕЛЛ — НЕ РАБОТА ЗАПУСКА. Их приносит фоновый прогрев
// библиотеки: он идёт по новеллам в порядке важности, уступает открытой главе
// и живой поверхности и начинается через три секунды. Одна работа — один дом.
func TestBootPrefetchDoesNotWalkEveryTitle(t *testing.T) {
	const dl = "../../../unity/Packages/com.lvn.engine/Runtime/Content/DownloadManager.cs"
	raw, err := os.ReadFile(dl)
	if err != nil {
		t.Skipf("нет %s: %v", dl, err)
	}
	src := string(raw)

	boot := strings.Index(src, "public async Task BootPrefetchAsync")
	if boot < 0 {
		t.Fatal("BootPrefetchAsync не найден — страж смотрит не туда")
	}
	end := strings.Index(src[boot:], "\n        }\n")
	if end < 0 {
		t.Fatal("не нашёл конец BootPrefetchAsync")
	}
	// Пояснения не код: страж, ловящий собственный докблок, ловит не то.
	var code strings.Builder
	for _, line := range strings.Split(src[boot:boot+end], "\n") {
		if s := strings.TrimSpace(line); strings.HasPrefix(s, "//") || strings.HasPrefix(s, "///") {
			continue
		}
		code.WriteString(line)
		code.WriteByte('\n')
	}
	body := code.String()

	if strings.Contains(body, "LvnParts.OfAll") {
		t.Error("запуск снова обходит ВСЕ новеллы: тридцать шесть запросов встанут " +
			"поперёк единственного нужного скрипта — того, в чью главу игрок входит")
	}
	if !strings.Contains(body, "showcaseAhead") {
		t.Error("прогрев запуска не спрашивает, будет ли витрина: обложки и фоны " +
			"чужих новелл поедут даже тому, кто уходит в воронку")
	}
}
