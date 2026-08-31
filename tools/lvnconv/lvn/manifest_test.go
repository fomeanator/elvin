package lvn

// ЧТО ЗДЕСЬ ЗАКРЕПЛЕНО.
//
// У манифеста до 31.08 гейта не было вовсе: скрипт проходил структурную
// проверку сервера, а manifest.json писали на диск после разбора JSON — и всё.
// А манифест — это весь облик приложения. Описка в имени поля не давала ни
// ошибки, ни строчки в логе: Newtonsoft молча пропускает незнакомое, экран
// брал умолчание, и автор искал причину глазами.
//
// Гейт появился, и теперь у него две обязанности, равные по важности:
// НАЗЫВАТЬ описку — и МОЛЧАТЬ на всём остальном. Гейт, который ругается на
// рабочий контент, хуже отсутствия гейта: его перестают читать. Поэтому
// половина тестов ниже проверяет не находки, а тишину.

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"
	"testing"
)

// manifestIssues прогоняет проверку по литералу манифеста.
func manifestIssues(t *testing.T, doc string) []Issue {
	t.Helper()
	return ValidateManifest([]byte(doc))
}

// mustBeQuiet требует ПОЛНОЙ тишины: ни одной находки. Это главная форма
// утверждения в этом файле — молчание проверяемо, а «не больше двух
// предупреждений» нет.
func mustBeQuiet(t *testing.T, doc string) {
	t.Helper()
	if got := manifestIssues(t, doc); len(got) != 0 {
		t.Fatalf("ожидалась тишина на %s, а гейт сказал: %v", doc, got)
	}
}

// ── ИМЕНА ПОЛЕЙ ──────────────────────────────────────────────────────────────

func TestОпечаткаВИмениПоляНазываетсяИПодсказывается(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":{"bg_colour":"#101010"}}}`)
	if !hasWarn(issues, "ui.hud.bg_colour — такого поля нет") {
		t.Fatalf("описка в имени поля прошла молча: %v", issues)
	}
	// Подсказка — половина ценности: «нет такого поля» без «может быть
	// bg_color» заставляет автора листать DTO.
	if !hasWarn(issues, `может быть "bg_color"`) {
		t.Fatalf("нет подсказки на близкое имя: %v", issues)
	}
}

func TestЗаконноеИмяПоляНеТревожит(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"#101010","height":0.08,"show_progress":true}}}`)
}

// КАТАЛОГ ПРОВЕРЯЕТСЯ ТЕМ ЖЕ СНИМКОМ, ЧТО И ОБЛИК. Схема снимается с двух
// исходников (LvnUiConfig.cs — облик, LvnManifest.cs — каталог), и спуск идёт
// по ТИПАМ полей от корня LvnManifest, а не по одному поддереву `ui`. Для
// игрока это один файл, и описка в имени новеллы стоит ровно столько же.
func TestОпечаткаВКаталогеЛовитсяТакЖеКакВОблике(t *testing.T) {
	issues := manifestIssues(t, `{"titles":[{"id":"t","nmae":"Полночь"}]}`)
	if !hasWarn(issues, "titles[0].nmae — такого поля нет") {
		t.Fatalf("описка в каталоге прошла молча: %v", issues)
	}
	if !hasWarn(issues, `может быть "name"`) {
		t.Fatalf("нет подсказки на близкое имя: %v", issues)
	}
}

// ПРО ЧТО СНИМКА НЕТ — МОЛЧИМ, А НЕ ВРЁМ. `assets` описан LvnAssetMeta, он
// лежит в отдельном исходнике и в снимок не попадает. Класс неизвестен —
// значит, имена внутри не проверяются вовсе: объявить чужое поле
// несуществующим хуже, чем промолчать.
func TestПроНеизвестныйКлассИменаНеПроверяются(t *testing.T) {
	mustBeQuiet(t, `{"assets":{"ui/logo.png":{"чего-то-эдакое":1,"bytes":10}}}`)
}

// КЛЮЧИ АВТОРСКОГО СЛОВАРЯ — НЕ ПОЛЯ. `sprites` — это Dictionary<string, …>,
// его ключи суть ИМЕНА ГЕРОЕВ. Судить их по схеме значит объявить
// несуществующими всех персонажей игры; проверять надо значения.
func TestКлючиАвторскогоСловаряНеСчитаютсяПолями(t *testing.T) {
	mustBeQuiet(t, `{"sprites":{"mira":{"name":"Мира"},"дорн":{"name":"Дорн"}}}`)
}

func TestВнутриАвторскогоСловаряЗначенияВсёЖеПроверяются(t *testing.T) {
	issues := manifestIssues(t, `{"sprites":{"mira":{"nmae":"Мира"}}}`)
	if !hasWarn(issues, "sprites.mira.nmae — такого поля нет") {
		t.Fatalf("значение словаря должно проверяться по схеме: %v", issues)
	}
}

// Заметка автора в JSON. Комментариев в языке нет, и их пишут ключом на `$`
// (та же конвенция, что в grammar.json). Поле это или заметка — видно по
// первому символу, и ругаться на неё гейт не вправе.
func TestЗаметкаНаДолларНеПоле(t *testing.T) {
	mustBeQuiet(t, `{"$note":"половина примера","ui":{"$why":"так надо","hud":{"height":0.08}}}`)
}

// ── ЦВЕТА ────────────────────────────────────────────────────────────────────

func TestЦветСловомИШестнадцатеричныйМолчат(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"accent"}}}`)
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"#ff00aa"}}}`)
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"#FF00AA80"}}}`)
	// Регистр и пробелы вокруг — не повод для находки.
	mustBeQuiet(t, `{"ui":{"hud":{"pill_text_color":" ACCENT "}}}`)
}

// Правило цвета читает не только хвост `_color`, но и поле, которое ЗОВЁТСЯ
// «color» целиком — так подписан цвет имени героя в каталоге спрайтов.
func TestПолеНазванноеПростоЦветомСудитсяТакЖе(t *testing.T) {
	mustBeQuiet(t, `{"sprites":{"mira":{"color":"#ffcc00"}}}`)
	if !hasWarn(manifestIssues(t, `{"sprites":{"mira":{"color":"мутный"}}}`), "не цвет") {
		t.Fatal("мусор в цвете имени героя прошёл молча")
	}
}

func TestМусорВЦветеЛовитсяСПодсказкой(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":{"bg_color":"accnt"}}}`)
	if !hasWarn(issues, "не цвет") {
		t.Fatalf("мусор в цвете прошёл молча: %v", issues)
	}
	if !hasWarn(issues, `может быть "accent"`) {
		t.Fatalf("нет подсказки на близкое слово цвета: %v", issues)
	}
}

// НЕЗАКРЫТАЯ ПОДСТАНОВКА — НЕ ОПЕЧАТКА. `{theme.bg}` ещё не подставили; это
// шаблон, а не значение, и гейт обязан пропустить его молча.
func TestНеподставленнаяПодстановкаНеСчитаетсяОшибкой(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"{theme.bg}"}}}`)
}

// ── ЗАКРЫТЫЕ СЛОВА ───────────────────────────────────────────────────────────

func TestЗакрытоеСловоПоИмениПоля(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"browse":{"theme":"midnigt"}}}`)
	if !hasWarn(issues, `ui.browse.theme="midnigt" — такого значения нет`) {
		t.Fatalf("неизвестная тема прошла молча: %v", issues)
	}
	if !hasWarn(issues, `может быть "midnight"`) {
		t.Fatalf("нет подсказки на близкую тему: %v", issues)
	}
	mustBeQuiet(t, `{"ui":{"browse":{"theme":"cyber"}}}`)
}

// «mode» СЛИШКОМ ОБЩЕЕ ИМЯ, чтобы судить о нём по одному слову: оно бывает и у
// анимации. Поэтому закрытый список привязан к ПОЛНОМУ ПУТИ.
func TestЗакрытоеСловоПоПолномуПути(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":{"mode":"иногда"}}}`)
	if !hasWarn(issues, `ui.hud.mode="иногда" — такого значения нет`) {
		t.Fatalf("неизвестный режим HUD прошёл молча: %v", issues)
	}
	if !hasWarn(issues, "известны: always, full, choices") {
		t.Fatalf("находка не перечисляет известные значения: %v", issues)
	}
	mustBeQuiet(t, `{"ui":{"hud":{"mode":"choices"}}}`)
}

// И обратное: то же слово «mode» ВНЕ этого пути не судится вовсе — иначе
// авторский `mode=queue` у анимации стал бы находкой на ровном месте.
func TestТоЖеИмяВнеПутиНеСудится(t *testing.T) {
	mustBeQuiet(t, `{"assets":{"anim/idle.json":{"mode":"queue"}}}`)
}

func TestЗакрытыеСловаПоявленияИВспышки(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"dialogue":{"appear":"rise"}}}`)
	if !hasWarn(manifestIssues(t, `{"ui":{"dialogue":{"appear":"взлёт"}}}`), "такого значения нет") {
		t.Fatal("неизвестное появление диалогового окна прошло молча")
	}
	mustBeQuiet(t, `{"ui":{"stage":{"tap_burst":"hearts","speaker_focus":"solo"}}}`)
	if !hasWarn(manifestIssues(t, `{"ui":{"stage":{"speaker_focus":"тускло"}}}`), "такого значения нет") {
		t.Fatal("неизвестная подсветка говорящего прошла молча")
	}
}

// ПОРЯДОК ДВУХ ПРОВЕРОК: имя судится ПЕРВЫМ. Значит, закрытый словарь имеет
// смысл только у поля, которое в схеме есть, — иначе автор получит «такого
// поля нет» и до разбора значения дело не дойдёт никогда. Тест держит эту
// связку: словарь без поля — правило, которое не может сработать.
func TestУКаждогоЗакрытогоСловаряЕстьПолеВСхеме(t *testing.T) {
	var s ManifestSchema
	if err := json.Unmarshal(manifestFieldsJSON, &s); err != nil {
		t.Fatalf("снимок схемы не разбирается: %v", err)
	}
	has := func(name string) bool {
		for _, fields := range s {
			if _, ok := fields[name]; ok {
				return true
			}
		}
		return false
	}
	// ИЗВЕСТНЫЙ МЁРТВЫЙ СЛОВАРЬ. `box_appear` не встречается НИГДЕ, кроме
	// самого правила: ни в DTO, ни в рантайме, ни в контенте, ни в документации
	// (появление диалогового окна автор пишет как `ui.dialogue.appear`).
	// Правило безвредно, но сработать не может — и, пока оно висит, читатель
	// думает, что такое поле бывает. Уберут его — тест потребует убрать и
	// строчку отсюда.
	dead := map[string]string{"box_appear": "нигде не встречается; появление окна — это ui.dialogue.appear"}
	for field := range ManifestWords {
		if has(field) {
			if why, ok := dead[field]; ok {
				t.Errorf("поле %q в схеме появилось (%s) — уберите его из списка мёртвых словарей", field, why)
			}
			continue
		}
		if _, known := dead[field]; known {
			continue
		}
		t.Errorf("закрытый словарь объявлен для поля %q, которого в снимке схемы нет. "+
			"Сработать он не может: имя проверяется раньше значения, и автор получит "+
			"«такого поля нет». Либо поле переименовали в DTO, либо словарь пора убрать.", field)
	}
	for path := range ManifestWordsByPath {
		seg := path[strings.LastIndex(path, ".")+1:]
		if !has(seg) {
			t.Errorf("закрытый словарь по пути %q ссылается на поле %q, которого в снимке схемы нет", path, seg)
		}
	}
}

// ── ФОРМА ВХОДА ──────────────────────────────────────────────────────────────

// КРИВОЙ JSON — ЭТО ОШИБКА, А НЕ ПРЕДУПРЕЖДЕНИЕ. Разница не косметическая:
// предупреждения гейт пропускает, ошибки блокируют. Манифест, который не
// разбирается, — это «приложение не откроется», и молча доехать до игрока он
// не должен.
func TestКривойJSONЭтоОшибкаАНеПредупреждение(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":`)
	if len(issues) != 1 {
		t.Fatalf("на нечитаемом JSON ждали ровно одну находку, получили %d: %v", len(issues), issues)
	}
	if issues[0].Sev != SevError {
		t.Fatalf("нечитаемый JSON обязан быть ошибкой, а он %v", issues[0].Sev)
	}
	if !hasError(issues, "не разбирается как JSON") {
		t.Fatalf("находка не объясняет причину: %v", issues)
	}
}

// Пустое и отсутствующее не роняют проверку: манифест «ещё не наполнили» —
// законное состояние, а паника в гейте закрыла бы автору дорогу совсем.
func TestПустойИОтсутствующийМанифестНеРоняют(t *testing.T) {
	for _, doc := range []string{`{}`, `null`, `[]`, `{"ui":null}`, `{"titles":[]}`, `{"ui":{"hud":{}}}`} {
		if got := ValidateManifest([]byte(doc)); len(got) != 0 {
			t.Fatalf("на %s ждали тишину, получили %v", doc, got)
		}
	}
}

// ВЛОЖЕННОСТЬ И МАССИВЫ. Путь в находке — единственное, по чему автор найдёт
// место в файле на тысячу строк, поэтому индекс элемента в нём обязателен.
func TestВложенностьИМассивыОбходятсяСПолнымПутём(t *testing.T) {
	issues := manifestIssues(t, `{"titles":[
	  {"id":"a"},
	  {"id":"b","seasons":[{"chapters":[{"id":"c1"},{"id":"c2","bg_colour":"#101010"}]}]}
	]}`)
	if !hasWarn(issues, "titles[1].seasons[0].chapters[1].bg_colour — такого поля нет") {
		t.Fatalf("описка на глубине четырёх уровней не найдена или путь неполон: %v", issues)
	}
}

// Глубина ограничена сверху, и это не должно выглядеть как падение: очень
// вложенный (или зациклённый генератором) манифест обязан просто закончиться.
func TestОченьГлубокийМанифестНеВешаетПроверку(t *testing.T) {
	deep := strings.Repeat(`{"ui":`, 200) + `{}` + strings.Repeat(`}`, 200)
	_ = ValidateManifest([]byte(deep)) // важно только то, что вернулись
}

// ── СНИМОК СХЕМЫ ─────────────────────────────────────────────────────────────

// Разбор нарочно грубый: он обязан видеть ровно то, что видит Newtonsoft, —
// ПУБЛИЧНЫЕ ПОЛЯ ДАННЫХ. Статика, константы и readonly в манифест не попадают
// никогда, и попади они в схему — гейт разрешил бы автору писать то, чего
// рантайм не прочтёт.
func TestСнимокБерётПоляДанныхИНеБерётСтатику(t *testing.T) {
	src := `
    public sealed class LvnUiConfig
    {
        public Dictionary<string, CurrencyLook> currency_look;
        public string guest_name;
        public LvnUiConfig ui;
        public static bool Ready;
        public const int NoNumber = 0;
        public static readonly string Cached = "x";
    }`
	got := ScrapeManifestSchema(src)["LvnUiConfig"]
	want := map[string]string{
		"currency_look": "map:CurrencyLook",
		"guest_name":    "string",
		"ui":            "LvnUiConfig",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("снимок разошёлся с DTO:\n получили %v\n ждали   %v", got, want)
	}
}

// СЛОВАРЬ ПОМЕЧАЕТСЯ ОСОБО, СПИСОК — НЕТ. У списка элемент — объект схемы, у
// словаря ключ авторский. Пометка `map:` и есть та разница, из-за отсутствия
// которой гейт объявлял несуществующими имена героев.
func TestСловарьСводитсяКЗначениюИПомечаетсяАСписокНет(t *testing.T) {
	src := `
    public sealed class LvnManifest
    {
        public Dictionary<string, LvnAssetMeta> assets;
        public List<LvnTitle> titles;
        public List<string> languages;
    }`
	got := ScrapeManifestSchema(src)["LvnManifest"]
	want := map[string]string{
		"assets":    "map:LvnAssetMeta",
		"titles":    "LvnTitle",
		"languages": "string",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("сведение обобщённых типов разошлось:\n получили %v\n ждали   %v", got, want)
	}
}

// Полное имя типа сводится к последнему сегменту: в снимке классы лежат под
// короткими именами, и `Lvn.Content.LvnCost` обязан найти там же, где `LvnCost`.
func TestВложенноеИмяТипаСводитсяКПоследнемуСегменту(t *testing.T) {
	src := `
    public sealed class LvnTitle
    {
        public Lvn.Content.LvnCost cost;
        public System.Collections.Generic.List<string> languages;
        public System.Collections.Generic.Dictionary<string, Lvn.Content.LvnChapter> chapters;
    }`
	got := ScrapeManifestSchema(src)["LvnTitle"]
	want := map[string]string{
		"cost":      "LvnCost",
		"languages": "string",
		"chapters":  "map:LvnChapter",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("длинное имя типа не сведено:\n получили %v\n ждали   %v", got, want)
	}
}

// ИМЯ В JSON БЫВАЕТ НЕ ИМЕНЕМ ПОЛЯ. `var` — ключевое слово C#, и в DTO оно
// объявлено как storyVar с псевдонимом. Схема обязана знать то имя, которое
// пишет АВТОР: иначе гейт объявит несуществующим поле из живого манифеста.
func TestПсевдонимJsonИмениПопадаетВСнимокВместоИмениПоля(t *testing.T) {
	src := `
    public sealed class LvnWardrobeSlot
    {
        [Newtonsoft.Json.JsonProperty("var")]
        public string storyVar;
        public string name;
    }`
	got := ScrapeManifestSchema(src)["LvnWardrobeSlot"]
	if got["var"] != "string" {
		t.Fatalf("псевдоним JSON-имени не попал в снимок: %v", got)
	}
	if _, ok := got["storyVar"]; ok {
		t.Fatalf("имя поля C# не должно подменять авторское имя: %v", got)
	}
	// Псевдоним действует ровно на одно поле, а не на все следующие.
	if got["name"] != "string" {
		t.Fatalf("поле после псевдонима потерялось: %v", got)
	}
}

// Поле с умолчанием — обычное поле данных. Пока разбор о нём не знал, `duration`
// анимации выглядела несуществующей.
func TestПолеСУмолчаниемНеТеряется(t *testing.T) {
	src := `
    public sealed class LvnAnim
    {
        public float duration = 1f;
        public bool loop = true;
    }`
	got := ScrapeManifestSchema(src)["LvnAnim"]
	want := map[string]string{"duration": "float", "loop": "bool"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("поле с умолчанием потеряно:\n получили %v\n ждали   %v", got, want)
	}
}

// ── ГЛАВНОЕ: ТИШИНА НА ЖИВОМ КОНТЕНТЕ ────────────────────────────────────────

// ГЕЙТ, КОТОРЫЙ РУГАЕТСЯ НА РАБОЧИЙ КОНТЕНТ, ХУЖЕ ОТСУТСТВИЯ ГЕЙТА: его
// перестают читать, и вместе с шумом теряется настоящая находка. Поэтому
// проверка обязана молчать на манифестах, которые мы САМИ показываем авторам
// как образец.
//
// Что в наборе: manifest.json под howto/, packages/ и sandbox/content/ — то,
// что мы сами показываем авторам как образец.
//
// Чего в наборе НЕТ и почему:
//
//   - `*/Packages/manifest.json` — это файл Unity (список UPM-зависимостей).
//     Имя совпало, содержимое чужое; сервер его и не видит — isManifestPath
//     узнаёт только manifest.json в корне контента.
//   - `server/content/manifest.json` — прод-снимок. Он разбирается отдельно,
//     соседним тестом со СПИСКОМ известных находок: часть из них — мёртвые
//     данные в проде, а часть — настоящая ложная тревога гейта, и смешивать их
//     с образцами для авторов нельзя. Когда список опустеет, файл вернётся
//     сюда.
//   - `.history/` и `node_modules/` — прошлые версии и чужой код.
func TestГейтМолчитНаАвторскихПримерах(t *testing.T) {
	root := repoRoot(t)
	var checked int
	for _, sub := range []string{"howto", "packages", filepath.Join("sandbox", "content")} {
		base := filepath.Join(root, sub)
		if _, err := os.Stat(base); err != nil {
			continue
		}
		err := filepath.Walk(base, func(p string, info os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if info.IsDir() {
				switch info.Name() {
				case ".history", "node_modules", ".git", "Library":
					return filepath.SkipDir
				}
				return nil
			}
			if info.Name() != "manifest.json" {
				return nil
			}
			// Тёзка из Unity — не наш манифест.
			if filepath.Base(filepath.Dir(p)) == "Packages" {
				return nil
			}
			blob, rerr := os.ReadFile(p)
			if rerr != nil {
				t.Errorf("%s не читается: %v", p, rerr)
				return nil
			}
			checked++
			rel, _ := filepath.Rel(root, p)
			for _, is := range ValidateManifest(blob) {
				t.Errorf("гейт ругается на авторский пример %s: %s\n"+
					"Ложная тревога на образце опаснее пропуска: по этим файлам "+
					"учатся, и «у меня в примере тоже ругается» отучает читать гейт.",
					rel, is.Msg)
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", sub, err)
		}
	}
	// Набор, который случайно опустел, зелёный всегда и не проверяет ничего.
	if checked == 0 {
		t.Fatal("не нашлось ни одного авторского manifest.json — тест стал пустым, поправьте корни обхода")
	}
}

// Прод-снимок из набора выше исключён намеренно, но исчезнуть из виду он не
// должен: этот тест держит ЗАКРЫТЫЙ СПИСОК известных находок. Появится новая —
// тест назовёт её; исчезнет старая — потребует убрать строчку. Так исключение
// остаётся временным, а не превращается в слепое пятно.
//
// В списке два РАЗНЫХ рода находок, и путать их нельзя:
//
//	МЁРТВЫЕ ДАННЫЕ В ПРОДЕ — поле в манифесте есть, рантайм его не читает:
//	  seasons[].id / seasons[].name  у LvnSeason есть только chapters;
//	  titles[].description           описание живёт в card.description;
//	  titles[].bg_url                bg_url есть у ГЛАВЫ, а не у новеллы.
//
//	ЛОЖНАЯ ТРЕВОГА САМОГО ГЕЙТА — поле законное, а в схеме его нет:
//	  rev     сервер сам вписывает его в манифест (manifestRevGate) и сам же
//	          требует обратно при следующем PUT. Значит, КАЖДОЕ сохранение
//	          манифеста из панели получает эту находку — на ровном месте.
//	  models  список моделей внутри 3D-набора. Он лежит в прод-манифесте 16 раз,
//	          но в репозитории нет НИ ОДНОГО места, которое его пишет или
//	          читает: ни Lvn3DBundle, ни сервер, ни конвертер. Скорее всего —
//	          след инструмента сборки наборов, которого здесь уже нет.
func TestИзвестныеНаходкиПродаНеРастут(t *testing.T) {
	root := repoRoot(t)
	p := filepath.Join(root, "server", "content", "manifest.json")
	blob, err := os.ReadFile(p)
	if err != nil {
		t.Skipf("прод-снимка нет рядом (%v) — сверять нечего", err)
	}
	// `rev` из этого списка УШЁЛ: поле дописывает сам сервер, и находка на
	// каждом сохранении была ложной тревогой — теперь оно объявлено служебным
	// (lvn.ServerAddedKeys). Список ведёт только НАСТОЯЩИЕ мёртвые данные.
	known := []struct {
		re  *regexp.Regexp
		why string
	}{
		{regexp.MustCompile(`^sets3d\.[^.]+\.platforms\.[^.]+\.models$`), "список моделей набора, Lvn3DBundle о нём не знает"},
		{regexp.MustCompile(`^titles\[\d+\]\.seasons\[\d+\]\.id$`), "у LvnSeason есть только chapters"},
		{regexp.MustCompile(`^titles\[\d+\]\.seasons\[\d+\]\.name$`), "у LvnSeason есть только chapters"},
		{regexp.MustCompile(`^titles\[\d+\]\.description$`), "описание живёт в card.description"},
		{regexp.MustCompile(`^titles\[\d+\]\.bg_url$`), "bg_url есть у главы, а не у новеллы"},
	}
	hit := make([]bool, len(known))
	for _, is := range ValidateManifest(blob) {
		// Находка начинается с пути: «ui.hud.mode="х" — …» или «rev — …».
		path := is.Msg
		if i := strings.Index(path, " — "); i > 0 {
			path = path[:i]
		}
		if i := strings.Index(path, "="); i > 0 {
			path = path[:i]
		}
		matched := false
		for i, k := range known {
			if k.re.MatchString(path) {
				hit[i], matched = true, true
				break
			}
		}
		if !matched {
			t.Errorf("в прод-манифесте находка, которой нет в списке известных: %s\n"+
				"Либо контент испортили, либо гейт стал ругаться на законное поле — "+
				"разберитесь, прежде чем дописывать строчку в список.", is.Msg)
		}
	}
	for i, k := range known {
		if !hit[i] {
			t.Errorf("известная находка %q в прод-манифесте исчезла (%s) — уберите её из списка; "+
				"когда список опустеет, верните server/content/manifest.json в набор авторских примеров",
				k.re, k.why)
		}
	}
}

// Снимок, лежащий рядом данными, должен ОСТАВАТЬСЯ данными: если он перестанет
// разбираться, гейт молча потеряет все имена и начнёт пропускать любые описки.
func TestСнимокСхемыЧитаетсяИНеПуст(t *testing.T) {
	var s ManifestSchema
	if err := json.Unmarshal(manifestFieldsJSON, &s); err != nil {
		t.Fatalf("снимок схемы не разбирается: %v", err)
	}
	if len(s) < 15 {
		t.Fatalf("в снимке всего %d классов — гейт почти ничего не проверяет", len(s))
	}
	if len(s["LvnUiConfig"]) == 0 || len(s["LvnManifest"]) == 0 {
		t.Fatal("в снимке нет корневых классов LvnManifest/LvnUiConfig — проверять будет нечего")
	}
}
