package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// СТРАЖИ СЛОВАРЕЙ ЯЗЫКА — цвет, именованные места, свойства анимации, переходы
// фигуры, стили ауры, виды узлов дерева `ui`, встроенные функции выражений,
// закрытые слова манифеста.
//
// У каждого понятия несколько зеркал: рантайм, компилятор, валидатор,
// подсказки редактора, веб-плеер, экспортированная игра. Правда — у того, кто
// ИСПОЛНЯЕТ; остальные сверяются с ним, и не только по составу, но и по
// значениям: «yellow» разошёлся между рантаймами при полном совпадении слов.

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

func TestАдресРазбираетДомАНеМестоВызова(t *testing.T) {
	roots := []string{
		filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine", "Runtime"),
		filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine.shell", "Runtime"),
	}
	bare := regexp.MustCompile(`StartsWith\("(https?|file|jar)`)
	// СРЕЗАНИЕ ЗАПРОСА — то же правило, только с другого конца адреса.
	// «Найти '?' и отрезать хвост» жило самодельной копией в офлайн-политике,
	// и копия эта отличалась от дома: дом чистит адрес через LvnUrl.Bare, а
	// копия — руками, из-за чего рядом (в имени каталога перевода) запрос
	// вообще не отрезался и файл не находился. Правило одно, дом один.
	query := regexp.MustCompile(`IndexOf\('\?'\)`)
	for _, root := range roots {
		err := filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			if strings.HasSuffix(path, "LvnUrl.cs") {
				return nil // дом и есть место, где правило записано
			}
			raw, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			code := stripCommentsAndStrings(string(raw))
			if m := query.FindString(code); m != "" {
				t.Fatalf("%s: запрос из адреса срезают на месте (%q) — возьмите LvnUrl.Bare.\n"+
					"Самодельная копия уже расходилась с домом: рядом, в имени каталога "+
					"перевода, запрос не отрезался вовсе, и файл молча не находился",
					filepath.Base(path), m)
			}
			if m := bare.FindString(code); m != "" {
				t.Fatalf("%s: %q — схему адреса спрашивают у LvnUrl.\n"+
					"Пока это пишут на месте, правил становится столько же, сколько мест",
					filepath.Base(path), m)
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
}

// Веб-плеер знает те же слова цвета, что и движок.
//
// Плеер отдавал слово ПРЯМО В CSS: «accent», «warm», «sepia» браузер молча
// игнорирует, а «green» в CSS — ТЁМНЫЙ #008000, тогда как в движке зелёный
// яркий. Один и тот же скрипт в приложении и по Share-ссылке давал разный
// зелёный — расхождение рантаймов, которое видит только игрок.

func TestВебПлеерЗнаетТеЖеСловаЦвета(t *testing.T) {
	root := repoRoot(t)
	csRaw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "UiColor.cs"))
	if err != nil {
		t.Fatal(err)
	}
	cs := string(csRaw)
	at := strings.Index(cs, "public static Color Named(")
	end := strings.Index(cs, "public static Color Token(")
	if at < 0 || end < at {
		t.Fatal("UiColor.Named пропал — словарь держится на нём")
	}
	engine := map[string]bool{}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(cs[at:end], -1) {
		engine[m[1]] = true
	}
	jsRaw, err := os.ReadFile(filepath.Join(root, "panel", "public", "play", "color.js"))
	if err != nil {
		t.Fatal(err)
	}
	js := map[string]bool{}
	// Слова — ключи трёх таблиц словаря; значения бывают любые, ключи всегда
	// в начале строки после отступа.
	for _, m := range regexp.MustCompile(`(?m)^  ([a-z_]+): `).FindAllStringSubmatch(string(jsRaw), -1) {
		js[m[1]] = true
	}
	if len(js) < 20 {
		t.Fatalf("в словаре плеера всего %d слов — похоже, якорь сторожа промахнулся", len(js))
	}
	for w := range engine {
		if !js[w] {
			t.Fatalf("движок знает цвет %q, а веб-плеер нет — одна и та же глава "+
				"в приложении и по ссылке покрасится по-разному", w)
		}
	}
	for w := range js {
		if !engine[w] {
			t.Fatalf("веб-плеер знает цвет %q, которого нет у движка — "+
				"расхождение в другую сторону, но того же рода", w)
		}
	}

	// СЛОВ МАЛО — НУЖНЫ ЗНАЧЕНИЯ. Первая версия этого сторожа сверяла только
	// состав, и подмена «green» на HTML-ный тёмный проходила зелёной: ровно
	// тот баг, ради которого всё делалось, мог вернуться молча. А «yellow»
	// уже успел разойтись — у движка это Unity-шный (1, 0.922, 0.016), у
	// плеера был чистый #ffff00.
	//
	// Сверяем ту треть словаря, где значение — константа движка, а не тема:
	// имена движка. Токены темы у площадки СВОИ намеренно (облик её), а
	// мнемоники заданы долями и сравнивать их по строке нечестно.
	engineHex := map[string]string{
		"white": "#ffffff", "black": "#000000", "red": "#ff0000", "blue": "#0000ff",
		"green": "#00ff00", "yellow": "#ffeb04", "cyan": "#00ffff", "magenta": "#ff00ff",
	}
	jsHex := map[string]string{}
	for _, m := range regexp.MustCompile(`(?m)^  ([a-z_]+): "(#[0-9a-f]{6})",`).FindAllStringSubmatch(string(jsRaw), -1) {
		jsHex[m[1]] = m[2]
	}
	for w, want := range engineHex {
		got, ok := jsHex[w]
		if !ok {
			t.Fatalf("у веб-плеера цвет %q задан не шестнадцатеричной константой — "+
				"сверить его с движком нечем", w)
		}
		if got != want {
			t.Fatalf("цвет %q: у движка %s, у веб-плеера %s.\n"+
				"Одна и та же вспышка красится по-разному в приложении и по ссылке — "+
				"а слова при этом совпадают, потому сторож и сверяет ЗНАЧЕНИЯ", w, want, got)
		}
	}
}

// Авторский цвет из манифеста читают СЛОВАРЁМ, а не hex-разбором.
//
// Сто три поля манифеста разбирались как «шестнадцать цифр», и
// `title_color: "accent"` молча не срабатывал — в скрипте то же слово
// работало, в манифесте нет, хотя пишет их один человек. Исключение ровно
// одно: сборка самой темы, откуда словарь звать нельзя (он спрашивает цвет у
// действующей темы, а она в этот момент ещё строится).

func TestАвторскийЦветЧитаютСловарём(t *testing.T) {
	allowed := map[string]string{
		"LvnTheme.cs":          "строит саму тему — словарь спросил бы у неё же",
		"LvnSpriteFxDriver.cs": "Html() зовут только литералами палитры эффектов",
		"UiColor.cs":           "дом разбора",
	}
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		root := filepath.Join(repoRoot(t), "unity", "Packages", pkg, "Runtime")
		err := filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			if _, ok := allowed[filepath.Base(path)]; ok {
				return nil
			}
			raw, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			if strings.Contains(stripCommentsAndStrings(string(raw)), "UiColor.Parse(") {
				t.Fatalf("%s: авторский цвет читают hex-разбором — он молчит на "+
					"слове из словаря, и автор не узнает, почему «не сработало»",
					filepath.Base(path))
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
}

// Словарь именованных мест один — во всех, кто на него отвечает.
//
// Отвечали шестью списками и четырьмя разными словарями. Движок знал
// center_left/center_right, но не знал offscreen_left — а его подсказывал
// редактор и принимал компилятор, и актёр, уведённый ЗА кадр, вставал в ЦЕНТР
// экрана. Компиляторы, наоборот, не знали center_left: слово молча становилось
// ЭМОЦИЕЙ. Веб-плеер знал три слова из девяти и своими числами.

func TestСловарьМестОдинВоВсехРантаймах(t *testing.T) {
	root := repoRoot(t)
	names := func(path string, re *regexp.Regexp, want int) map[string]bool {
		raw, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(path)))
		if err != nil {
			t.Fatal(err)
		}
		m := re.FindStringSubmatch(string(raw))
		if m == nil {
			t.Fatalf("%s: список мест не найден — поправь якорь сторожа", path)
		}
		out := map[string]bool{}
		for _, w := range regexp.MustCompile(`"?([a-z_]+)"?`).FindAllStringSubmatch(m[1], -1) {
			if strings.Contains(w[1], "_") || w[1] == "left" || w[1] == "right" || w[1] == "center" {
				out[w[1]] = true
			}
		}
		if len(out) != want {
			t.Fatalf("%s: мест %d, ожидалось %d — %v", path, len(out), want, out)
		}
		return out
	}
	// Движок — правда.
	truth := names("unity/Packages/com.lvn.engine/Runtime/UI/Placement.cs",
		regexp.MustCompile(`(?s)SlotNames\s*=\s*\{(.*?)\};`), 9)

	for _, side := range []struct {
		path string
		re   *regexp.Regexp
	}{
		{"unity/Packages/com.lvn.engine/Editor/LvnsCompiler.cs",
			regexp.MustCompile(`(?s)(case "left":.*?)ac\["position"\] = tok;`)},
		{"tools/lvnconv/internal/lvns/convert.go",
			regexp.MustCompile(`(?s)(case "left", "right".*?):\n`)},
		// В grammar.json список мест лежит ДВАЖДЫ: закрытые значения поля
		// `actor position=` (enums) и общие подсказки после `position=`
		// (attr_values). Сверяем оба — стоило разойтись одному, и редактор
		// подсказывал бы разное в двух шагах одного набора.
		{"tools/lvn-lang/src/grammar.json",
			regexp.MustCompile(`(?s)"enums".*?"position":\s*\[(.*?)\]`)},
		{"tools/lvn-lang/src/grammar.json",
			regexp.MustCompile(`(?s)"attr_values".*?"position":\s*\[(.*?)\]`)},
		{"panel/public/play/place.js",
			regexp.MustCompile(`(?s)const SLOTS = \{(.*?)\n\};`)},
	} {
		got := names(side.path, side.re, 9)
		for w := range truth {
			if !got[w] {
				t.Fatalf("%s: места %q не знает, а движок знает — "+
					"автор напишет его и получит не то место", side.path, w)
			}
		}
		for w := range got {
			if !truth[w] {
				t.Fatalf("%s: знает место %q, которого у движка нет", side.path, w)
			}
		}
	}
}

// У мест сверяются не только слова, но и ДОЛИ.
//
// Первая версия сторожа сверяла состав словаря — и веб-плеер мог знать те же
// девять имён с другими долями, оставаясь зелёным. Ровно так уже разошлись
// цвета: слова совпадали, «yellow» — нет.

func TestДолиМестСовпадаютСДвижком(t *testing.T) {
	root := repoRoot(t)
	csRaw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "Placement.cs"))
	if err != nil {
		t.Fatal(err)
	}
	body := regexp.MustCompile(`(?s)public static float SlotX\(string position\)(.*?)\n        \}`).
		FindStringSubmatch(string(csRaw))
	if body == nil {
		t.Fatal("SlotX не найден — поправь якорь сторожа")
	}
	engine := map[string]string{}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)": return (-?[0-9.]+)f;`).
		FindAllStringSubmatch(body[1], -1) {
		engine[m[1]] = strings.TrimSuffix(m[2], ".")
	}
	if len(engine) != 9 {
		t.Fatalf("в SlotX %d мест, ожидалось 9", len(engine))
	}
	jsRaw, err := os.ReadFile(filepath.Join(root, "panel", "public", "play", "place.js"))
	if err != nil {
		t.Fatal(err)
	}
	js := map[string]string{}
	for _, m := range regexp.MustCompile(`(?m)^  ([a-z_]+): (-?[0-9.]+),`).
		FindAllStringSubmatch(string(jsRaw), -1) {
		js[m[1]] = m[2]
	}
	num := func(s string) float64 {
		var f float64
		if _, err := fmt.Sscanf(s, "%g", &f); err != nil {
			t.Fatalf("не число: %q", s)
		}
		return f
	}
	for w, want := range engine {
		got, ok := js[w]
		if !ok {
			t.Fatalf("у веб-плеера нет доли для места %q", w)
		}
		if num(got) != num(want) {
			t.Fatalf("место %q: у движка %s, у веб-плеера %s.\n"+
				"Одна и та же сцена расставит героев по-разному в приложении и по ссылке", w, want, got)
		}
	}
}

// Что можно анимировать — один словарь на всех, кто про это спрашивают.
//
// Он жил ТОЛЬКО в рантайме (LvnAnimProp.Known). Компилятор `prop=` не смотрел,
// валидатор молчал, редактор не подсказывал — и «opacity» вместо «alpha»
// (каноническая описка, названная так в докблоке самого рантайма) проходило
// всю дорогу до игры, где трек молча пропускался. Полоса не двигалась, и
// искать было нечего.

func TestСловарьСвойствАнимацииОдин(t *testing.T) {
	root := repoRoot(t)
	csRaw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "LvnAnimProp.cs"))
	if err != nil {
		t.Fatal(err)
	}
	// Словарь КОНТЕКСТНЫЙ: у фигуры целиком свой набор, у слоя куклы свой.
	// Валидатор и подсказки знают объединение — но пусть знают ЕГО, а не
	// половину.
	engine := map[string]bool{}
	sides := map[string]map[string]bool{}
	for _, name := range []string{"Whole", "Layered"} {
		block := regexp.MustCompile(`(?s)` + name + ` = new HashSet<string>\s*\{(.*?)\n        \};`).
			FindStringSubmatch(string(csRaw))
		if block == nil {
			t.Fatalf("LvnAnimProp.%s не найден — поправь якорь сторожа", name)
		}
		sides[name] = map[string]bool{}
		for _, m := range regexp.MustCompile(`"([a-z_]+)"`).FindAllStringSubmatch(block[1], -1) {
			engine[m[1]] = true
			sides[name][m[1]] = true
		}
	}
	// КОНТЕКСТ ТОЖЕ СВЕРЯЕТСЯ. Проверка была плоской там, где исполнитель
	// контекстный: `frame` без слоя и `screen_x` со слоем проходили молча,
	// чтобы потом молча же ничего не сыграть.
	for side, want := range map[string][]string{"Whole": AnimPropsWhole, "Layered": AnimPropsLayered} {
		for _, w := range want {
			if !sides[side][w] {
				t.Fatalf("валидатор разрешает %q для %s, а рантайм там его не принимает", w, side)
			}
		}
		for w := range sides[side] {
			if !inSet(want, w) {
				t.Fatalf("рантайм принимает %q для %s, а валидатор пожалуется", w, side)
			}
		}
	}
	if len(engine) != 10 {
		t.Fatalf("в словаре свойств %d имён, ожидалось 10: %v", len(engine), engine)
	}
	for w := range engine {
		if !inSet(AnimProps, w) {
			t.Fatalf("рантайм умеет анимировать %q, а валидатор об этом не знает — "+
				"опечатка рядом с ним пройдёт молча", w)
		}
	}
	for _, w := range AnimProps {
		if !engine[w] {
			t.Fatalf("валидатор считает %q свойством, а рантайм его не знает — "+
				"пропустит трек и промолчит", w)
		}
	}
	gRaw, err := os.ReadFile(filepath.Join(root, "tools", "lvn-lang", "src", "grammar.json"))
	if err != nil {
		t.Fatal(err)
	}
	list := regexp.MustCompile(`(?s)"attr_values".*?"prop":\s*\[(.*?)\]`).FindStringSubmatch(string(gRaw))
	if list == nil {
		t.Fatal("grammar.json: подсказок для prop= нет — автору неоткуда узнать словарь")
	}
	ide := map[string]bool{}
	for _, m := range regexp.MustCompile(`"([a-z_]+)"`).FindAllStringSubmatch(list[1], -1) {
		ide[m[1]] = true
	}
	for w := range engine {
		if !ide[w] {
			t.Fatalf("редактор не подсказывает свойство %q, которое движок умеет", w)
		}
	}
	for w := range ide {
		if !engine[w] {
			t.Fatalf("редактор подсказывает свойство %q, которого движок не знает — "+
				"автор напишет его и получит неподвижную полосу", w)
		}
	}
}

// Как фигура входит и уходит — словарь один, и разница с панелями видна.
//
// Знал его только рантайм (VnStage.ParseTransition), и незнакомое слово давало
// не ошибку, а TransitionType.None: появление БЕЗ перехода, молча. Автор,
// выучивший `slide_up` на панелях `ui`, писал его актёру и получал мгновенное
// возникновение без единого слова — наборы у панели и у фигуры разные.

func TestСловарьПереходовФигурыОдин(t *testing.T) {
	root := repoRoot(t)
	raw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "VnStage.Reads.cs"))
	if err != nil {
		t.Fatal(err)
	}
	body := regexp.MustCompile(`(?s)ParseTransition\(string name\)(.*?)\n        \}`).
		FindStringSubmatch(string(raw))
	if body == nil {
		t.Fatal("ParseTransition не найден — поправь якорь сторожа")
	}
	engine := map[string]bool{"": true} // пустое значение законно: поле без слова
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(body[1], -1) {
		engine[m[1]] = true
	}
	if len(engine) < 10 {
		t.Fatalf("в ParseTransition %d слов — похоже, якорь промахнулся: %v", len(engine), engine)
	}
	for w := range engine {
		if !inSet(ActorTransitions, w) {
			t.Fatalf("рантайм понимает переход %q, а валидатор его отвергнет — "+
				"жалоба на законное слово", w)
		}
	}
	for _, w := range ActorTransitions {
		if !engine[w] {
			t.Fatalf("валидатор считает %q переходом, а рантайм его не знает — "+
				"фигура появится БЕЗ перехода и промолчит", w)
		}
	}
	// Наборы фигуры и панели РАЗНЫЕ намеренно — но пусть это будет видно, а не
	// обнаружено автором на экране.
	appearRaw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "LvnAppear.cs"))
	if err != nil {
		t.Fatal(err)
	}
	panel := map[string]bool{}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(string(appearRaw), -1) {
		panel[m[1]] = true
	}
	if panel["slide_up"] && engine["slide_up"] {
		t.Fatalf("наборы сошлись — обнови этот сторож и канон: разница больше не разница")
	}
}

// Стили ауры: валидатор знает СИНОНИМЫ, а не только канонические имена.
//
// Синонимы (`ice`, `thunder`, `dark`, `void`…) знал только рантайм, и
// валидатор ругался на законное слово: `aura_style=ice` получал «is not a
// known value» да ещё и совет «may be fire?» — прямо противоположную стихию.
// Ложная тревога дороже молчания: автор идёт править работающий код.

func TestСтилиАурыВключаяСинонимы(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "World", "LvnSpriteFxDriver.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := string(raw)
	from := strings.Index(src, `case "guard":`)
	to := strings.Index(src[from:], "default:")
	if from < 0 || to < 0 {
		t.Fatal("разбор aura_style не найден — поправь якорь сторожа")
	}
	engine := map[string]bool{}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(src[from:from+to], -1) {
		engine[m[1]] = true
	}
	if len(engine) < 20 {
		t.Fatalf("стилей ауры всего %d — похоже, якорь промахнулся", len(engine))
	}
	for w := range engine {
		if !inSet(AuraStyles, w) {
			t.Fatalf("рантайм понимает стиль %q, а валидатор ругается на него — "+
				"жалоба на работающий код, да ещё и с советом взять другую стихию", w)
		}
	}
	for _, w := range AuraStyles {
		if !engine[w] {
			t.Fatalf("валидатор считает %q стилем, а рантайм его не знает — "+
				"возьмёт basic и напишет об этом только в лог", w)
		}
	}
}

// Вид узла дерева `ui` — словарь, а не тайна рантайма.
//
// Неизвестный вид не давал ошибки: LvnUiLayer падал в `default` и делал ПУСТУЮ
// ПАНЕЛЬ. Опечатка «buton» превращала кнопку в невидимый прямоугольник — экран
// собирался, кнопки на нём не было, и в логе ни строчки.

func TestВидыУзловДереваUiОдинСловарь(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "LvnUiLayer.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := stripCommentsAndStrings(string(raw))
	// Виды, у которых своя ветка: от разбора `kind` до `default`.
	from := strings.Index(string(raw), `string kind = (string)n["kind"]`)
	if from < 0 {
		t.Fatal("разбор kind не найден — поправь якорь сторожа")
	}
	to := strings.Index(string(raw)[from:], "default:")
	if to < 0 {
		t.Fatal("default у видов узлов не найден")
	}
	own := map[string]bool{}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(string(raw)[from:from+to], -1) {
		own[m[1]] = true
	}
	if len(own) < 5 {
		t.Fatalf("видов со своей веткой всего %d — похоже, якорь промахнулся: %v", len(own), own)
	}
	for w := range own {
		if !inSet(UiNodeKinds, w) {
			t.Fatalf("рантайм умеет узел %q, а валидатор о нём не знает — "+
				"пожалуется на работающее дерево", w)
		}
	}
	// Контейнеры живут в `default` и по коду не отличимы — они перечислены в
	// валидаторе вручную, и это единственное, что тут можно проверить.
	for _, container := range []string{"panel", "row", "column"} {
		if !inSet(UiNodeKinds, container) {
			t.Fatalf("контейнер %q пропал из словаря — автор получит жалобу на самый частый узел", container)
		}
		if own[container] {
			t.Fatalf("у %q появилась своя ветка — обнови комментарий словаря: "+
				"он утверждает, что контейнеры живут в default", container)
		}
	}
	_ = src
}

// Вшитая в расширение копия грамматики — КОПИЯ, а не форк.
//
// Её не обновлял никто, и она отстала настолько, что учила НЕСУЩЕСТВУЮЩЕМУ:
// предлагала `obj fill`, `fill_from`, `in3d`, `world` — поля, которых в движке
// нет, — и не знала `cutscene`, `ui`, `track`. Автор, поставивший расширение,
// получал подсказки к другому языку. Копия, которую никто не пересобирает,
// хуже отсутствия копии.
//
// Обновляется тем же `npm run gen`, что и сама грамматика.

func TestВстроенныеФункцииСверяютсяСДвижком(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine",
		"Runtime", "LvnExpression.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := string(raw)
	from := strings.Index(src, `case "rand":`)
	if from < 0 {
		t.Fatal("разбор функций не найден — поправь якорь сторожа")
	}
	to := strings.Index(src[from:], "default:")
	if to < 0 {
		t.Fatal("default у функций не найден")
	}
	engine := map[string]bool{}
	for _, m := range regexp.MustCompile(`case "([a-z_0-9]+)"`).FindAllStringSubmatch(src[from:from+to], -1) {
		engine[m[1]] = true
	}
	if len(engine) < 20 {
		t.Fatalf("встроенных функций всего %d — похоже, якорь промахнулся", len(engine))
	}
	for name := range engine {
		if !ExprFuncs[name] {
			t.Fatalf("движок умеет %s(), а валидатор о ней не знает — "+
				"пожалуется на работающее выражение", name)
		}
	}
	for name := range ExprFuncs {
		if engine[name] || HostExprFuncs[name] {
			continue
		}
		t.Fatalf("валидатор считает %s() существующей, но движок её не встраивает "+
			"и в HostExprFuncs её нет — выражение молча вычислится в ничто", name)
	}
	// Функции хозяина не должны случайно оказаться встроенными: тогда чистый
	// com.lvn.engine перестал бы отличаться от приложения, и обещание про
	// «безопасный пустой ответ» стало бы неправдой.
	for name := range HostExprFuncs {
		if engine[name] {
			t.Fatalf("%s() объявлена функцией хозяина, но движок её встраивает — "+
				"обнови HostExprFuncs и комментарий про чистый пакет", name)
		}
	}
}

// Закрытое слово из манифеста читают через дом, а не switch с молчаливым default.
//
// Манифест НЕ проходит через структурный гейт — в отличие от скриптов, — и об
// опечатке в нём сказать больше некому: ни компилятор, ни валидатор его не
// читают. Значит, говорит тот, кто исполняет. Раньше он молчал: опечатка в
// теме отдавала «Полночь», и киберпанковая игра открывалась в облике по
// умолчанию.

func TestЗакрытоеСловоАвтораЧитаютЧерезДом(t *testing.T) {
	root := repoRoot(t)
	// Дом на месте и жалуется один раз на пару «поле + слово».
	home, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "LvnAuthorWord.cs"))
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{`_said.Add(field + "=" + w)`, "LogWarning"} {
		if !strings.Contains(string(home), want) {
			t.Fatalf("в LvnAuthorWord пропало %q — дисциплина жалобы держится на нём", want)
		}
	}
	// Известные потребители спрашивают ЕГО.
	for _, c := range []struct{ path, anchor string }{
		{filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime", "UI", "LvnTheme.cs"), "ui.browse.theme"},
		{filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime", "UI", "LvnAppear.cs"), `"appear"`},
		{filepath.Join("unity", "Packages", "com.lvn.engine.shell", "Runtime", "NovelShell.cs"), "ui.hud.mode"},
		{filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime", "UI", "VnStage.cs"), "ui.stage.tap_burst"},
		{filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime", "UI", "VnStage.Dialogue.cs"), "ui.stage.speaker_focus"},
	} {
		raw, err := os.ReadFile(filepath.Join(root, c.path))
		if err != nil {
			t.Fatal(err)
		}
		src := string(raw)
		if !strings.Contains(src, "LvnAuthorWord.Pick") || !strings.Contains(src, c.anchor) {
			t.Fatalf("%s: закрытое слово читают мимо дома — опечатка автора снова "+
				"обернётся молчаливым умолчанием", filepath.Base(c.path))
		}
	}
}

// Словарь цвета у проверки манифеста — тот же, что у движка.
//
// Манифест не проходил через гейт вовсе, и `title_color: "acccent"` молча
// давал умолчание. Теперь проверка есть, но она сама стала ЕЩЁ ОДНИМ зеркалом
// словаря — значит, обязана сверяться с исполнителем, как все прочие.
func TestСловарьЦветаПроверкиМанифестаСовпадаетСДвижком(t *testing.T) {
	raw, err := os.ReadFile(filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine",
		"Runtime", "UI", "UiColor.cs"))
	if err != nil {
		t.Fatal(err)
	}
	cs := string(raw)
	at := strings.Index(cs, "public static Color Named(")
	end := strings.Index(cs, "public static Color Token(")
	if at < 0 || end < at {
		t.Fatal("UiColor.Named пропал — словарь держится на нём")
	}
	engine := map[string]bool{}
	for _, m := range regexp.MustCompile(`case "([a-z_]+)":`).FindAllStringSubmatch(cs[at:end], -1) {
		engine[m[1]] = true
	}
	atLeast(t, len(engine), 20, "слов словаря цвета")
	for w := range engine {
		if !inSet(ColorWords, w) {
			t.Fatalf("движок знает цвет %q, а проверка манифеста нет — "+
				"пожалуется на работающее поле", w)
		}
	}
	for _, w := range ColorWords {
		if !engine[w] {
			t.Fatalf("проверка манифеста считает %q цветом, а движок его не знает", w)
		}
	}
}

// Закрытые слова манифеста у проверки — те же, что читает рантайм.
func TestЗакрытыеСловаМанифестаСовпадаютСРантаймом(t *testing.T) {
	root := repoRoot(t)
	read := func(rel string) string {
		b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
		if err != nil {
			t.Fatal(err)
		}
		return string(b)
	}
	// Каждое слово, которое проверка считает допустимым, обязано стоять в
	// вызове LvnAuthorWord.Pick у того, кто это поле читает.
	for _, c := range []struct{ field, file string }{
		{"theme", "unity/Packages/com.lvn.engine/Runtime/UI/LvnTheme.cs"},
		{"speaker_focus", "unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Dialogue.cs"},
		{"tap_burst", "unity/Packages/com.lvn.engine/Runtime/UI/VnStage.cs"},
	} {
		src := read(c.file)
		for _, w := range ManifestWords[c.field] {
			if !strings.Contains(src, `"`+w+`"`) {
				t.Fatalf("проверка манифеста разрешает %s=%q, а %s этого слова не знает — "+
					"автор напишет его и получит умолчание", c.field, w, filepath.Base(c.file))
			}
		}
	}
	shell := read("unity/Packages/com.lvn.engine.shell/Runtime/NovelShell.cs")
	for _, w := range ManifestWordsByPath["ui.hud.mode"] {
		if !strings.Contains(shell, `"`+w+`"`) {
			t.Fatalf("проверка манифеста разрешает ui.hud.mode=%q, а оболочка его не знает", w)
		}
	}
}
