package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Убирает строки-комментарии: страж однажды нашёл искомое в собственном
// объяснении и промолчал о живом дефекте.
func hudCode(t *testing.T, rel string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(repoRoot(t), rel))
	if err != nil {
		t.Fatalf("не читается %s: %v", rel, err)
	}
	var out []string
	for _, ln := range strings.Split(string(b), "\n") {
		if s := strings.TrimSpace(ln); strings.HasPrefix(s, "//") || strings.HasPrefix(s, "///") {
			continue
		}
		out = append(out, ln)
	}
	return strings.Join(out, "\n")
}

// БИБЛИОТЕКА ГРЕЕТСЯ ОБОЗОМ, А НЕ ПО ОДНОМУ ФАЙЛУ.
//
// Было: `foreach (...) await WarmOne(part.Url)` — 2399 файлов строго друг за
// другом при полосе шириной двенадцать. Игрок видел «в очереди: файлов 1» и
// скорость, падающую в ноль на каждой границе, потому что счёт пакета никто
// не вёл: одиночная загрузка обещает только себя.
func TestПрогревБиблиотекиИдётПачками(t *testing.T) {
	const rel = "unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.Chapter.cs"
	src := hudCode(t, rel)

	i := strings.Index(src, "WarmLibraryAsync")
	if i < 0 {
		t.Fatalf("%s: не найден WarmLibraryAsync — страж потерял предмет охраны", rel)
	}
	body := src[i:]

	if !strings.Contains(body, "StartPreloadBatch") {
		t.Errorf("%s: прогрев библиотеки не зовёт StartPreloadBatch.\n"+
			"Без обоза счётчики пакета остаются нулевыми, и индикатор показывает\n"+
			"единственный файл в полёте вместо всей очереди.", rel)
	}
	if strings.Contains(body, "await _assets.Loader.DownloadAssetBytes") {
		t.Errorf("%s: прогрев снова качает файлы по одному (DownloadAssetBytes в теле).\n"+
			"Полоса сети шириной %d простаивает, а очередь не видна индикатору.", rel, 12)
	}
}

// КОЛЬЦО МЕРЯЕТ ВЕСЬ ПАКЕТ.
//
// Считалось «принято / ожидается» по файлам В ПОЛЁТЕ. Пока обоз шёл по одному,
// это случайно совпадало с правдой; в десять полос кольцо стало заполняться и
// откатываться на каждой десятке из сотни.
func TestКольцоСчитаетВесьПакет(t *testing.T) {
	const rel = "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.cs"
	src := hudCode(t, rel)

	if !strings.Contains(src, "BatchClosedBytes") {
		t.Fatalf("%s: нет счёта закрытых байт пакета — кольцу нечем мерить очередь", rel)
	}
	i := strings.Index(src, "public TransferSnapshot Transfers()")
	if i < 0 {
		t.Fatalf("%s: не найден Transfers() — страж потерял предмет охраны", rel)
	}
	body := src[i:]
	if j := strings.Index(body, "\n        }"); j > 0 {
		body = body[:j]
	}
	// ПЛАН — ПЕРВЫЙ ОТВЕТ. Веса файлов известны из манифеста до первого байта,
	// и знаменатель берётся оттуда: догадка «принято + 64 КБ × непочатые»
	// занижала его на порядок и держала кольцо у полного (замер — 61,9 п.п.,
	// qa/download-progress-check.sh).
	if !strings.Contains(body, "exp = BatchPlannedBytes") {
		t.Errorf("%s: в Transfers() доля считается не планом пакета.\n"+
			"Кольцо снова уедет к полному на первых процентах.", rel)
	}
	// Догадка остаётся ЗАПАСНОЙ веткой — для пакетов, чьих весов никто не дал.
	for _, want := range []string{"rec += BatchClosedBytes", "exp += BatchClosedBytes"} {
		if !strings.Contains(body, want) {
			t.Errorf("%s: в Transfers() нет «%s».\n"+
				"Кольцо снова покажет долю горсти в полёте, а не всей очереди,\n"+
				"и скорость будет проваливаться в ноль на границе файлов.", rel, want)
		}
	}
}

// ПРИЕЗД НЕ РАЗЫГРЫВАЕТСЯ ПОВЕРХ ГОТОВОГО КАДРА.
//
// Церемония входа прячет всех, кого выставила глава, показывает героиню и
// возвращает спрятанных. На начале главы сцена пуста — так и задумано. При
// возврате с сохранения кадр уже собран реплеем, и церемония сводится к
// «спрятать собеседника и вернуть собеседника»: четыре растворения по 0,2 с
// плюс почти секунда паузы ради кадра, который и до них был правильным.
func TestПриездСпрашиваетОдетаЛиСцена(t *testing.T) {
	const rel = "unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.Portal.cs"
	src := hudCode(t, rel)

	// ТОЛЬКО СВОЙ МЕТОД. В файле две церемонии — уход в главу и приезд в неё, —
	// и обе зовут BeginSoloAsync. Поиск по всему файлу цеплялся за первую и
	// объявлял верный порядок нарушенным.
	m := strings.Index(src, "private async Task ArriveInChapterAsync()")
	if m < 0 {
		t.Fatalf("%s: не найден ArriveInChapterAsync — страж потерял предмет охраны", rel)
	}
	src = src[m:]

	ask := strings.Index(src, "StoryDressedStage")
	if ask < 0 {
		t.Fatalf("%s: приезд не спрашивает StoryDressedStage.\n"+
			"Поверх кадра, собранного реплеем, церемония переставляет то,\n"+
			"что и так стоит правильно: прячет собеседника и возвращает его.", rel)
	}
	// Вопрос должен стоять ДО того, как катсцена спрячет кадр истории: после
	// BeginSoloAsync ответ будет о пустоте, которую она сама и устроила.
	if solo := strings.Index(src, "BeginSoloAsync"); solo >= 0 && ask > solo {
		t.Errorf("%s: вопрос «одета ли сцена» задан ПОСЛЕ BeginSoloAsync.\n"+
			"К этому мгновению кадр истории уже спрятан, и ответ всегда «пусто».", rel)
	}
}
