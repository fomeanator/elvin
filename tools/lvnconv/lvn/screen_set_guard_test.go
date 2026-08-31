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

// Вкладку не адресуют голым числом.
//
// Вкладка была размазана по пяти перечням: место в ряду, подпись, страница,
// цвет полотна и вызовы вида TabGoTo(1) с пояснением в комментарии. Теперь
// набор один (LvnTabs), а сторож следит, чтобы номера не вернулись.
func TestВкладкуНеАдресуютГолымЧислом(t *testing.T) {
	dir := shellRuntimeDir(t)
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	bare := regexp.MustCompile(`TabGoTo\(\s*\d`)
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") || e.Name() == "LvnTabs.cs" {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		if m := bare.FindString(stripCommentsAndStrings(string(raw))); m != "" {
			t.Fatalf("%s: %q — номер вкладки живёт в LvnTabs, а не в месте вызова "+
				"(иначе смысл числа опять уедет в комментарий рядом)", e.Name(), m)
		}
	}
	// Подпись вкладки собирает набор, а не хаб своим switch.
	nav, err := os.ReadFile(filepath.Join(dir, "BrowseHub.Nav.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := stripCommentsAndStrings(string(nav))
	for _, gone := range []string{`"nav.home"`, `"nav.store"`, `"nav.wardrobe"`, `"nav.profile"`, `"nav.gallery"`} {
		if strings.Contains(string(nav), gone) {
			t.Fatalf("BrowseHub.Nav.cs: %s снова здесь — слово вкладки берут у LvnTabs", gone)
		}
	}
	if !strings.Contains(src, "LvnTabs.Shown") {
		t.Fatalf("BrowseHub.Nav.cs: ряд вкладок собирается не по набору — место вкладки опять задаёт рука")
	}
	// Число страниц ленты не зашито.
	shell, err := os.ReadFile(filepath.Join(dir, "NovelShell.Navigation.cs"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(shell), "LvnTabs.PageCount") {
		t.Fatalf("NovelShell.Navigation.cs: сколько у ленты страниц — знает набор вкладок, не ограничитель на месте")
	}
}

// «Что с этой главой» спрашивают у дома, а не собирают на месте.
//
// Правило одно на все три списка глав, но два из них собирали свою половину
// сами: брали достигнутую главу и номер первой и звали Швейцара напрямую.
// Половины уже расходились — свой расчёт рисовал замок на первой главе
// непочатой новеллы, рядом с играбельной кнопкой.
func TestСостояниеГлавыСпрашиваютУДома(t *testing.T) {
	dir := shellRuntimeDir(t)
	// Экраны, показывающие СПИСОК глав.
	for _, name := range []string{"TitleDetailScreen.cs", "TitleDetailScreen.Restart.cs", "TitleCarousel.cs"} {
		raw, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			t.Fatal(err)
		}
		src := stripCommentsAndStrings(string(raw))
		if strings.Contains(src, "LvnGatekeeper.ChapterOpen") {
			t.Fatalf("%s: экран снова зовёт Швейцара напрямую — состояние главы "+
				"целиком отвечает LvnChapterMarks, иначе половины опять разойдутся", name)
		}
		if !strings.Contains(src, "LvnChapterMarks") {
			t.Fatalf("%s: список глав рисуется мимо дома состояний", name)
		}
	}
}

// Заголовок раздела собирают ОДИН раз.
//
// Он строился четырьмя способами: две частные копии в двух экранах (30 и 28
// кеглем) и два набора строк на месте. Двое из четверых пропускали огранку
// темы — и на «Кибере» половина заголовков шла капсом с разрядкой, а половина
// обычным текстом, в одном и том же экране.
func TestЗаголовокРазделаСобираютОдинРаз(t *testing.T) {
	dir := shellRuntimeDir(t)
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Признак самодельного заголовка: жирная подпись кеглем «как у раздела»
	// в одном методе. Ищем по соседству, а не построчно.
	bold := regexp.MustCompile(`(?s)fontSize = Lvn\.UI\.LvnFonts\.Size\(30f\);\s*\n\s*[\w.]+\.style\.unityFontStyleAndWeight = FontStyle\.Bold`)
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") || e.Name() == "ScreenUi.cs" {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		src := stripCommentsAndStrings(string(raw))
		if !bold.MatchString(src) {
			continue
		}
		// Кнопка «Играть» тоже жирная и того же кегля — она не заголовок.
		if strings.Contains(e.Name(), "TitleDetailScreen.cs") || strings.Contains(e.Name(), "LvnTopBar") ||
			strings.Contains(e.Name(), "BootVeil") || strings.Contains(e.Name(), "ProfileScreen.Account") {
			continue
		}
		t.Fatalf("%s: похоже на самодельный заголовок раздела — его собирает "+
			"ScreenUi.SectionHeader, и только он накладывает огранку темы", e.Name())
	}
	ui, err := os.ReadFile(filepath.Join(dir, "ScreenUi.cs"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(ui), "LvnChrome.Heading(lbl)") {
		t.Fatalf("ScreenUi.SectionHeader перестал накладывать огранку темы — " +
			"на «Кибере» заголовок обязан идти капсом с разрядкой")
	}
}

// Словарь цвета один — и у движка, и у подсказок редактора.
//
// Их было четыре: дерево `ui` знало токены темы, команды кадра — имена движка
// и мнемоники настроения, поля команд — только шестнадцать цифр, а подсказки
// редактора — вторую треть набора. Автор писал одно слово в трёх местах и в
// двух получал молчание: «эффект не сработал», хотя цвета просто не нашли.
func TestСловарьЦветаОдинВездеГдеЕгоПишут(t *testing.T) {
	root := repoRoot(t)
	src, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "UiColor.cs"))
	if err != nil {
		t.Fatal(err)
	}
	// Слова словаря — все `case "..."` внутри Named.
	named := map[string]bool{}
	at := strings.Index(string(src), "public static Color Named(")
	if at < 0 {
		t.Fatal("UiColor.Named пропал — словарь цвета держится на нём")
	}
	end := strings.Index(string(src), "public static Color Token(")
	if end < 0 || end < at {
		end = len(src)
	}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(string(src)[at:end], -1) {
		named[m[1]] = true
	}
	if len(named) < 20 {
		t.Fatalf("в словаре цвета всего %d слов — похоже, якорь сторожа промахнулся", len(named))
	}
	// Грамматика — ОДНА, и это grammar.json: grammar.js из неё генерируется
	// (npm run gen), править его руками бесполезно.
	rel := filepath.Join("tools", "lvn-lang", "src", "grammar.json")
	raw, err := os.ReadFile(filepath.Join(root, rel))
	if err != nil {
		t.Fatal(err)
	}
	list := regexp.MustCompile(`(?s)"color":\s*\[(.*?)\]`).FindStringSubmatch(string(raw))
	if list == nil {
		t.Fatalf("%s: подсказок для color= нет вовсе", rel)
	}
	got := map[string]bool{}
	for _, m := range regexp.MustCompile(`"([a-z_]+)"`).FindAllStringSubmatch(list[1], -1) {
		got[m[1]] = true
	}
	for w := range named {
		if !got[w] {
			t.Fatalf("%s: движок знает цвет %q, а редактор его не подсказывает —\n"+
				"автор не узна́ет о слове, которое работает", rel, w)
		}
	}
	for w := range got {
		if !named[w] {
			t.Fatalf("%s: редактор подсказывает цвет %q, которого движок не знает —\n"+
				"автор напишет его и получит умолчание с жалобой в журнале", rel, w)
		}
	}
}

// Сгенерированная грамматика не отстаёт от своей правды.
//
// grammar.js делает npm run gen из grammar.json, но правку JSON без перегона
// ничто не ловило: узел `portal` жил в правде и не доезжал до подсказок —
// автор про целую команду просто не знал. Проверка есть и в node-тестах
// пакета, но их не гоняет ни run-all, ни CI движка.
func TestПодсказкиНеОтстаютОтГрамматики(t *testing.T) {
	root := repoRoot(t)
	jsonRaw, err := os.ReadFile(filepath.Join(root, "tools", "lvn-lang", "src", "grammar.json"))
	if err != nil {
		t.Fatal(err)
	}
	jsRaw, err := os.ReadFile(filepath.Join(root, "tools", "lvn-lang", "src", "grammar.js"))
	if err != nil {
		t.Fatal(err)
	}
	names := func(src, key, open, close string) map[string]bool {
		m := regexp.MustCompile(`(?s)` + regexp.QuoteMeta(key) + open + `(.*?)\n` + close).FindStringSubmatch(src)
		if m == nil {
			t.Fatalf("не нашёл список %q — поправь якорь сторожа", key)
		}
		out := map[string]bool{}
		for _, w := range regexp.MustCompile(`"([a-z_0-9]+)"`).FindAllStringSubmatch(m[1], -1) {
			out[w[1]] = true
		}
		return out
	}
	truth := names(string(jsonRaw), `"ops":`, ` \[`, `  \],`)
	shown := names(string(jsRaw), `export const OPS =`, ` \[`, `\];`)
	for w := range truth {
		if !shown[w] {
			t.Fatalf("команду %q грамматика знает, а подсказки нет — забыли `npm run gen` "+
				"в tools/lvn-lang после правки grammar.json", w)
		}
	}
	for w := range shown {
		if !truth[w] {
			t.Fatalf("подсказки предлагают %q, чего в grammar.json нет — "+
				"grammar.js правили руками, а он генерируемый", w)
		}
	}
}

// Компилятор .lvns разложен по темам — и режут его РАЗБОРОМ, а не подсчётом.
//
// Файл был крупнейшим в репозитории (1703 строки) и неучтённым нарушением
// собственного правила канона. Две попытки разрезать его подсчётом фигурных
// скобок развалили сборку: в компиляторе полно строковых литералов со
// скобками — он про них и написан. Сторож держит разложение и заодно ловит
// возврат к «одному файлу на всё».
func TestКомпиляторРазложенПоТемам(t *testing.T) {
	dir := filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine", "Editor")
	for _, f := range []string{"LvnsCompiler.Expand.cs", "LvnsCompiler.Anim.cs"} {
		if _, err := os.Stat(filepath.Join(dir, f)); err != nil {
			t.Fatalf("%s пропал — темы компилятора снова съехались в один файл", f)
		}
	}
	// Развороты живут в .Expand: в корне их быть не должно.
	core, err := os.ReadFile(filepath.Join(dir, "LvnsCompiler.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := stripCommentsAndStrings(string(core))
	for _, gone := range []string{"static string ExpandLoops(", "static string ExpandCalls(", "static JObject BuildAnimCmd("} {
		if strings.Contains(src, gone) {
			t.Fatalf("LvnsCompiler.cs: %q вернулся в корень — у него свой файл", gone)
		}
	}
	// Правило канона, из-за которого всё и делалось.
	lines := strings.Count(string(core), "\n") + 1
	if lines > 1400 {
		t.Fatalf("LvnsCompiler.cs снова разросся (%d строк): файл в тысячу строк — "+
			"это несколько классов, которые забыли разделить", lines)
	}
}
