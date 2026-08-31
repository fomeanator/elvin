package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

func shellRuntimeDir(t *testing.T) string {
	t.Helper()
	return filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine.shell", "Runtime")
}

// Оболочка не ведёт список своих экранов от руки.
//
// Он был написан поимённо в ShowOnly, и дописать туда новый экран забывали:
// таблица лидеров, экран конца главы и гардеробная вкладка в него так и не
// попали. Держался перечень на втором таком же — на том, что каждый экран ещё
// и прячут сразу после создания. Теперь набор ведёт себя сам (LvnScreenSet), а
// сторож следит, чтобы ручной перечень не вернулся.
func TestShowOnlyНеПеречисляетЭкраныПоимённо(t *testing.T) {
	path := filepath.Join(shellRuntimeDir(t), "NovelShell.Navigation.cs")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	src := stripCommentsAndStrings(string(raw))
	at := strings.Index(src, "void ShowOnly()")
	if at < 0 {
		t.Fatalf("%s: ShowOnly переименован — поправь якорь сторожа", path)
	}
	tail := src[at:]
	if end := strings.Index(tail, "\n\n"); end > 0 {
		tail = tail[:end]
	}
	named := regexp.MustCompile(`\bHide\(\s*[A-Z]\w*\s*\)|\b[A-Z]\w*\s*\?\.\s*Hide\(\)`)
	if m := named.FindString(tail); m != "" {
		t.Fatalf("ShowOnly снова перечисляет экраны от руки (%q).\n"+
			"Набор ведёт себя сам: новый экран вносят через Add, а не дописывают сюда.\n%s", m, path)
	}
	if !strings.Contains(tail, "HideAll") {
		t.Fatalf("ShowOnly обязан убирать ВЕСЬ набор (_screens.HideAll()), а не отдельные экраны.\n%s", path)
	}
}

// Экран вносится в набор, осна́стка — нет: она переживает «убрать всё».
func TestЭкранВходитВНаборАОснасткаНет(t *testing.T) {
	path := filepath.Join(shellRuntimeDir(t), "NovelShell.Navigation.cs")
	if add := methodBody(t, path, "private void Add(VisualElement el)"); !strings.Contains(add, "_screens.Add(el)") {
		t.Fatalf("Add обязан записывать экран в набор — иначе «убрать всё» его пропустит")
	}
	if chrome := methodBody(t, path, "private void AddChrome(VisualElement el)"); strings.Contains(chrome, "_screens.Add") {
		t.Fatalf("осна́стка (верхний бар, кружок загрузок) в набор не входит: " +
			"она обязана пережить «убрать всё» — кружок виден из любого места")
	}
}

// У кого уход свой — тот уходит сам, и решает это НЕ место вызова.
func TestУходЭкранаРешаетсяОднимПравилом(t *testing.T) {
	dir := shellRuntimeDir(t)
	set, err := os.ReadFile(filepath.Join(dir, "LvnScreenSet.cs"))
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"interface ILvnHides", "is ILvnHides own", "own.Hide()"} {
		if !strings.Contains(string(set), want) {
			t.Fatalf("в LvnScreenSet пропало %q — единое правило ухода держится на нём", want)
		}
	}
	// Экран, чей Hide() делает БОЛЬШЕ, чем гасит показ (возвращает
	// непрозрачность, закрывает просмотрщик, снимает ожидание), обязан быть
	// помечен — иначе набор закроет его вполсилы.
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Метка наследуется: WardrobeTabScreen помечен через LvnOverlayScreen.
	decl := regexp.MustCompile(`(?:sealed |abstract |partial )*class (\w+)\s*:\s*([^\n{]+)`)
	marked := map[string]bool{}
	bases := map[string][]string{}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		for _, m := range decl.FindAllStringSubmatch(string(raw), -1) {
			var list []string
			for _, part := range strings.Split(m[2], ",") {
				part = strings.TrimSpace(part)
				if i := strings.LastIndex(part, "."); i >= 0 {
					part = part[i+1:]
				}
				if part == "ILvnHides" {
					marked[m[1]] = true
				}
				list = append(list, part)
			}
			bases[m[1]] = append(bases[m[1]], list...)
		}
	}
	var wears func(string, int) bool
	wears = func(name string, depth int) bool {
		if marked[name] {
			return true
		}
		if depth > 4 {
			return false
		}
		for _, b := range bases[name] {
			if b != name && wears(b, depth+1) {
				return true
			}
		}
		return false
	}
	sig := regexp.MustCompile(`public (?:virtual |override |sealed )*void Hide\(\)`)
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		path := filepath.Join(dir, e.Name())
		raw, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		src := stripCommentsAndStrings(string(raw))
		loc := sig.FindStringIndex(src)
		if loc == nil {
			continue
		}
		body := methodBody(t, path, src[loc[0]:loc[1]])
		lines := 0
		for _, l := range strings.Split(body, "\n") {
			if strings.TrimSpace(l) != "" {
				lines++
			}
		}
		if lines <= 1 {
			continue // гасит показ и только — метка не нужна
		}
		owner := decl.FindStringSubmatch(string(raw))
		if owner != nil && wears(owner[1], 0) {
			continue
		}
		t.Fatalf("%s: Hide() делает больше, чем гасит показ, но экран не помечен ILvnHides —\n"+
			"набор закроет его вполсилы: прозрачность, просмотрщик или ожидание останутся", e.Name())
	}
}

// «Назад» решает ОДИН — Режиссёр.
//
// Сцена спрашивала его, а оболочка перебирала признаки сама, и алерта в этой
// картине не было вовсе: поднятый над сюжетной панелью вопрос оставлял верхней
// панель, и «назад» закрывал её из-под вопроса. Теперь обе стороны читают одну
// стопку, а алерт в неё встаёт.
func TestНазадРешаетРежиссёрАНеПризнакиНаМесте(t *testing.T) {
	nav := filepath.Join(shellRuntimeDir(t), "NovelShell.Navigation.cs")
	body := methodBody(t, nav, "private void Update()")
	if !strings.Contains(body, "BackTarget") {
		t.Fatalf("оболочка снова решает «кто наверху» сама — спрашивать надо Режиссёра.\n%s", nav)
	}
	if strings.Contains(body, "Popup") {
		t.Fatalf("оболочка снова заглядывает в показ алерта напрямую — он поверхность Режиссёра.\n%s", nav)
	}
	popup := filepath.Join(shellRuntimeDir(t), "PopupScreen.cs")
	raw, err := os.ReadFile(popup)
	if err != nil {
		t.Fatal(err)
	}
	src := string(raw)
	for _, want := range []string{
		"LvnScreenDirector.Current.Open(Lvn.UI.LvnScreenDirector.Alert)",
		"LvnScreenDirector.Current.Close(Lvn.UI.LvnScreenDirector.Alert)",
	} {
		if !strings.Contains(src, want) {
			t.Fatalf("PopupScreen перестал вставать на стопку поверхностей (%q) — "+
				"без этого «назад» снова закроет панель из-под вопроса.\n%s", want, popup)
		}
	}
	// Признак «алерт открыт» пишется в одном месте — иначе стопка и экран
	// разойдутся, и разойдутся молча.
	if n := strings.Count(src, "_openFlag = "); n != 1 {
		t.Fatalf("PopupScreen: «алерт открыт» пишется в %d местах, а должно в одном "+
			"(через свойство _open, которое и ведёт стопку)", n)
	}
}

// Экран, который ЖДЁТ ответа, обязан уметь уйти.
//
// Экран конца главы держал ожидание всего цикла глав, а своего ухода не имел
// вовсе — и набор гасил ему показ мимо: экран исчезал, а цикл повисал. Сторож
// на непростой уход этого не ловил: тот смотрит только на экраны, у которых
// Hide() уже есть.
func TestЖдущийЭкранУмеетУйти(t *testing.T) {
	dir := shellRuntimeDir(t)
	// Экраны, которые оболочка вносит в набор: только с них и спрос.
	nav, err := os.ReadFile(filepath.Join(dir, "NovelShell.cs"))
	if err != nil {
		t.Fatal(err)
	}
	inSet := regexp.MustCompile(`(?m)\bAdd\((\w+)\)`)
	names := map[string]bool{}
	for _, m := range inSet.FindAllStringSubmatch(stripCommentsAndStrings(string(nav)), -1) {
		names[m[1]] = true
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	decl := regexp.MustCompile(`(?:sealed |abstract |partial )*class (\w+)\s*:`)
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		src := string(raw)
		if !strings.Contains(src, "TaskCompletionSource") {
			continue
		}
		owner := decl.FindStringSubmatch(src)
		if owner == nil {
			continue
		}
		// Имя поля в оболочке — имя класса без суффикса Screen; сверяем оба.
		short := strings.TrimSuffix(owner[1], "Screen")
		if !names[owner[1]] && !names[short] {
			continue // в набор не входит — уборка его не касается
		}
		if !strings.Contains(src, "ILvnHides") && !strings.Contains(src, "LvnOverlayScreen") {
			t.Fatalf("%s: экран ждёт ответа (TaskCompletionSource) и входит в набор, "+
				"но уходить не умеет — уборка погасит показ мимо него, и ждущий повиснет", e.Name())
		}
	}
}
