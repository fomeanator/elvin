package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

func stageUiFiles(t *testing.T) map[string]string {
	t.Helper()
	dir := filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine", "Runtime", "UI")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	out := map[string]string{}
	for _, e := range entries {
		if e.IsDir() || !strings.HasPrefix(e.Name(), "VnStage") || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		out[e.Name()] = string(raw)
	}
	if len(out) < 10 {
		t.Fatalf("нашлось всего %d частей VnStage — поправь якорь сторожа", len(out))
	}
	return out
}

// ЧЕГО СЦЕНА ЖДЁТ — СПРАШИВАЮТ ПО ИМЕНИ, А НЕ СКЛАДЫВАЮТ НА МЕСТЕ.
//
// Пара флагов ожидания (`wait` идёт / открыта форма ввода) складывалась в
// четырёх местах, и в каждом чуть по-своему. Пока их складывают руками,
// «а этот случай тоже сюда?» решают заново на каждом месте вызова — и решают
// по-разному. Теперь у вопроса два имени, StageBusy и TapNotOurs, и оба
// объявлены ровно один раз.
func TestОжиданиеСценыСкладываютТолькоВОдномМесте(t *testing.T) {
	files := stageUiFiles(t)
	// «Флаг ИЛИ флаг» — та самая ручная конъюнкция, ради которой всё делалось.
	pair := regexp.MustCompile(`_awaiting(?:Wait|Input)\s*\|\|\s*\(?\s*_awaiting(?:Wait|Input)`)
	for name, raw := range files {
		src := stripCommentsAndStrings(raw)
		n := len(pair.FindAllString(src, -1))
		if name == "VnStage.Playback.cs" {
			if n != 2 {
				t.Fatalf("VnStage.Playback.cs: конъюнкций ожидания %d, а должно быть ровно две — "+
					"объявления StageBusy и TapNotOurs. Третья значит, что кто-то опять сложил флаги руками", n)
			}
			continue
		}
		if n > 0 {
			t.Fatalf("%s: %q — пара флагов ожидания снова складывается на месте.\n"+
				"У вопроса есть имя: StageBusy («сцена занята сама собой») или TapNotOurs («тап не наш»).",
				name, pair.FindString(src))
		}
	}
}

// ОГОВОРКА ПРО ГОРЯЧИЕ ТОЧКИ ЗАПИСАНА ОДИН РАЗ.
//
// `wait` глотает касание — КРОМЕ экрана с горячими точками: там щелчок обязан
// дойти до точки и снять таймер, иначе поиск предмета замирает навсегда. Помнили
// оговорку в обоих местах, но ЗАПИСАНА она была дважды, и второй раз комментарием
// «то же, что выше». Такая пара расходится молча: правят одну половину.
func TestОговоркаПроГорячиеТочкиЖивётВОдномМесте(t *testing.T) {
	files := stageUiFiles(t)
	clause := regexp.MustCompile(`_awaitingWait\s*&&\s*_hotspots\.Count\s*==\s*0`)
	total := 0
	for name, raw := range files {
		n := len(clause.FindAllString(stripCommentsAndStrings(raw), -1))
		total += n
		if n > 0 && name != "VnStage.Playback.cs" {
			t.Fatalf("%s: оговорка про горячие точки написана здесь ВТОРОЙ раз.\n"+
				"Она часть ответа TapNotOurs — иначе половины разойдутся, и разойдутся молча", name)
		}
	}
	if total != 1 {
		t.Fatalf("оговорка «`wait` глотает касание, кроме экрана с точками» найдена %d раз(а), "+
			"а должна быть ровно одна — в TapNotOurs", total)
	}
}

// ЧЕТЫРЕ МЕСТА ВЫЗОВА СПРАШИВАЮТ ИМЕНАМИ.
//
// Сторож выше запрещает складывать флаги руками; этот следит, что места вызова
// действительно спрашивают — а не обходят вопрос, проверяя один флаг из двух.
// Забытая половина — это либо перемотка, проскочившая паузу автора, либо тап,
// съевший строку, которую игрок ещё печатает.
func TestМестаВызоваСпрашиваютНазваннымиОтветами(t *testing.T) {
	dir := filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine", "Runtime", "UI")
	for _, want := range []struct {
		file, sig, name, why string
	}{
		{"VnStage.Playback.cs", "private void SkipTick()", "StageBusy",
			"перемотка обязана вставать на `wait` и на форме ввода"},
		{"VnStage.Playback.cs", "private void AutoAdvanceTick()", "StageBusy",
			"авточтение обязано вставать на `wait` и на форме ввода"},
		{"VnStage.Pointer.cs", "private void OnPointerDown(PointerDownEvent evt)", "TapNotOurs",
			"нажатие не наше при форме ввода и при `wait` без горячих точек"},
		{"VnStage.Pointer.cs", "private void HandleTap(Vector2 pos)", "TapNotOurs",
			"и отпускание — по тому же правилу, а не по своей копии"},
	} {
		body := methodBody(t, filepath.Join(dir, want.file), want.sig)
		if !strings.Contains(body, want.name) {
			t.Fatalf("%s: %s перестал спрашивать %s — %s", want.file, want.sig, want.name, want.why)
		}
	}
}
