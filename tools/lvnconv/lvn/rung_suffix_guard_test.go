package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"testing"
)

// ИМЕНА СТУПЕНЕЙ — ДОГОВОР МЕЖДУ СЕРВЕРОМ И КЛИЕНТОМ, и он нигде не сверялся.
//
// Сервер делает уменьшенный вариант по имени: `hero@1k.png`. Клиент просит его
// ПО ТОМУ ЖЕ имени. Совпадать они обязаны буква в букву — но имена объявлены
// врозь: у клиента константами Q2k/Q1440/Q1k/QMini, у сервера
// downscaleSuffix/midSuffix/ecoSuffix/miniSuffix.
//
// Соседний страж (sheet_life_guard) проверяет, что ИСКЛЮЧЕНИЯ названы в обоих
// правилах, — но не значения самих ступеней. Замер 06.09: сверки значений нет
// нигде.
//
// Цена расхождения тихая и потому скверная: сервер соберёт `hero@eco.png`,
// клиент попросит `hero@1k.png`, получит 404 и откатится на полноразмерный
// исходник. Игра не сломается — просто вся экономия трафика исчезнет молча, и
// заметит это только тот, кто станет мерить байты.
func TestИменаСтупенейСовпадаютУКлиентаИСервера(t *testing.T) {
	root := repoRoot(t)
	read := func(rel string) string {
		b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
		if err != nil {
			t.Fatalf("не читается %s: %v", rel, err)
		}
		return string(b)
	}

	cs := read("unity/Packages/com.lvn.engine/Runtime/Content/DownloadPolicy.cs")
	go_ := read("server/downscale.go")

	// Пары: как ступень зовут у клиента и у сервера.
	пары := []struct{ what, csName, goName string }{
		{"крупная", "Q2k", "downscaleSuffix"},
		{"средняя", "Q1440", "midSuffix"},
		{"экономная", "Q1k", "ecoSuffix"},
		{"крошка", "QMini", "miniSuffix"},
	}

	значение := func(src, decl string) string {
		re := regexp.MustCompile(regexp.QuoteMeta(decl) + `\s*=\s*"([^"]+)"`)
		m := re.FindStringSubmatch(src)
		if m == nil {
			return ""
		}
		return m[1]
	}

	for _, p := range пары {
		client := значение(cs, p.csName)
		server := значение(go_, p.goName)
		if client == "" {
			t.Errorf("у клиента не нашлось имя ступени %s (%s) — страж потерял опору", p.what, p.csName)
			continue
		}
		if server == "" {
			t.Errorf("у сервера не нашлось имя ступени %s (%s) — страж потерял опору", p.what, p.goName)
			continue
		}
		if client != server {
			t.Errorf("ступень «%s» названа по-разному: клиент просит %q, сервер делает %q.\n"+
				"Клиент получит 404 и откатится на полноразмер — экономия трафика исчезнет молча.",
				p.what, client, server)
		}
	}
}
