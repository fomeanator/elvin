package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ОДИН СПИСОК ПАПОК, ТРИ МЕСТА — И НИ ОДНОГО СТРАЖА.
//
// «Крупный арт истории» (тот, которому положены ступени качества и коды для
// видеокарты) определяется ПО ПАПКЕ, и это правило записано трижды:
//
//	клиент     DownloadPolicy.LargeStoryArt   — строить ли уменьшенный вариант
//	сервер     ktx2LargeArt                   — собирать ли код для видеокарты
//	компилятор artOutsideRungFolders          — предупредить ли автора
//
// Расходятся такие копии не «когда-нибудь», а при добавлении пятой папки:
// впишут в клиент, забудут на сервере — и коды тихо перестанут собираться для
// нового арта. Игра при этом не ломается, просто игрок платит памятью.
//
// Отдельно горько, что третью копию завёл я сам в ночь 06.09, той же правкой,
// которая ЧИНИЛА молчаливые соглашения. Страж написан следом.
func TestПапкиКрупногоАртаСовпадаютВезде(t *testing.T) {
	root := repoRoot(t)
	folders := regexp.MustCompile(`"/(bg|art|sprites|spine|pixel|ui|cg|voice|audio)/"`)

	read := func(rel string) string {
		b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
		if err != nil {
			t.Fatalf("не читается %s: %v", rel, err)
		}
		return string(b)
	}

	// Вырезаем РОВНО тело правила: рядом в файлах лежат исключения (pixel/,
	// ui/) и объяснения к ним, и брать «до конца функции» значило бы
	// сравнивать не то. Первая редакция стража так и сделала — и покраснела на
	// собственной неточности, объявив расхождение там, где его нет.
	//
	// end задаёт, чем правило кончается: у C# это выражение-стрелка до «;»,
	// у Go — закрывающая скобка функции или литерала.
	body := func(src, from, end string) string {
		i := strings.Index(src, from)
		if i < 0 {
			t.Fatalf("не нашёл %q — правило переехало, страж потерял опору", from)
		}
		rest := src[i:]
		if j := strings.Index(rest, end); j > 0 {
			return rest[:j]
		}
		return rest
	}

	set := func(src string) []string {
		seen := map[string]bool{}
		for _, m := range folders.FindAllStringSubmatch(src, -1) {
			seen["/"+m[1]+"/"] = true
		}
		out := make([]string, 0, len(seen))
		for k := range seen {
			out = append(out, k)
		}
		sort.Strings(out)
		return out
	}

	клиент := set(body(read("unity/Packages/com.lvn.engine/Runtime/Content/DownloadPolicy.cs"),
		"public static bool LargeStoryArt", ";"))
	сервер := set(body(read("server/ktx2.go"), "func ktx2LargeArt", "\n}"))
	компилятор := set(body(read("tools/lvnconv/lvn/manifest.go"), "rung := []string{", "}"))

	if strings.Join(клиент, ",") != strings.Join(сервер, ",") {
		t.Errorf("клиент и сервер разошлись в папках крупного арта:\n  клиент: %v\n  сервер: %v\n"+
			"Коды для видеокарты перестанут собираться для нового арта — молча.", клиент, сервер)
	}
	if strings.Join(клиент, ",") != strings.Join(компилятор, ",") {
		t.Errorf("клиент и компилятор разошлись:\n  клиент:     %v\n  компилятор: %v\n"+
			"Автор получит предупреждение о папке, которая на самом деле работает — или НЕ получит о той, что нет.",
			клиент, компилятор)
	}
	if len(клиент) == 0 {
		t.Fatal("список папок пуст — страж читает не то место")
	}
	t.Logf("папки крупного арта (совпадают в трёх местах): %v", клиент)
}
