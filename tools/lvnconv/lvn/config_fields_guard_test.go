package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// НАСТРОЙКА, КОТОРУЮ НИКТО НЕ ЧИТАЕТ, — ЭТО ОБЕЩАНИЕ, КОТОРОГО ДВИЖОК НЕ ДАЁТ.
//
// Поля `LvnUiConfig` — публичная поверхность для автора: он видит их в схеме и
// в примерах, пишет в манифест и ждёт эффекта. Замер 28.08 нашёл 26 полей,
// которых не читал никто, и девять из них автор УЖЕ заполнил — осмысленными
// значениями («Как тебя зовут?», «Подтвердить», пять цветов формы ввода).
// Ошибки при этом не было нигде: ни в компиляторе, ни в логе, ни на экране.
// Ровно тот тихий отказ, ради борьбы с которым заведён весь этот пакет
// стражей.
//
// Тест держит список известных мёртвых полей ЯВНЫМ. Появилось новое —
// сборка красная: либо поле читают, либо его здесь называют вместе с причиной,
// по которой оно осталось. Автору от этого польза прямая: список ниже и есть
// честный ответ на вопрос «почему я это написал, а ничего не изменилось».

// Замер идёт ПО ИМЕНИ поля, а не по классу: имена вроде bg_color есть у многих
// блоков, и если хоть один из них читают, поле считается живым. Огрубление
// намеренное — задача стража поймать поле, которого не читает НИКТО, а не
// провести полный анализ достижимости.
var csDecl = regexp.MustCompile(`(?m)^\s*public\s+([^;{}()=]+?)\s*;`)

// Модификаторы, которые стоят между `public` и типом.
var csModifiers = map[string]bool{"static": true, "readonly": true, "const": true, "new": true, "volatile": true}

// declaredFields — ВСЕ имена объявления, а не последнее.
//
// Раньше здесь стоял разбор «тип, потом одно имя», и запись `public string
// enter_label, waiting_label, locked_label;` давала стражу ровно одно поле из
// трёх. Слепое пятно нашлось само: стоило убрать из такой строки мёртвый
// хвост, как на его место встало соседнее поле — и тоже оказалось мёртвым.
// Три подписи створа жили непроверенными ровно потому, что автор объявил их
// через запятую.
func declaredFields(src string) map[string]bool {
	out := map[string]bool{}
	for _, m := range csDecl.FindAllStringSubmatch(src, -1) {
		decl := []byte(m[1])
		// Обобщения прячут свои запятые: Dictionary<string, string> — один тип.
		depth := 0
		for i := 0; i < len(decl); i++ {
			switch decl[i] {
			case '<':
				depth++
			case '>':
				depth--
			case ',':
				if depth > 0 {
					decl[i] = ' '
				}
			}
		}
		words := strings.Fields(string(decl))
		for len(words) > 0 && csModifiers[words[0]] {
			words = words[1:]
		}
		if len(words) < 2 {
			continue // не поле: свойство, метод, вложенный тип
		}
		for _, name := range strings.Split(strings.Join(words[1:], " "), ",") {
			name = strings.TrimSpace(name)
			if name != "" && !strings.ContainsAny(name, " \t") {
				out[name] = true
			}
		}
	}
	return out
}

// Поля, которых сегодня не читает никто, и почему это осознанно.
//
// СПИСОК ПУСТ, и это результат разбора 31.08. Он держал 23 записи, и почти
// каждая была не оправданием, а описанием гнили: «прежний дизайн гардероба»,
// «баннер оплаты не построен», «цвет идёт из темы». Список честно называл
// мёртвое — и тем самым позволял ему жить дальше, потому что страж был зелёный.
//
// Разошлись по двум путям. Четыре подписи автор УЖЕ написал в живом манифесте
// («Надеть», «Надето», «Снять», «+{0} бонусом»), а игрок видел английские
// умолчания: их подхватил LvnAuthoredWords — поле секции переводится в ключ
// словаря там, один раз, вместо протяжки конфига через экраны. Остальные
// девятнадцать УДАЛЕНЫ из LvnUiConfig: поле, которого нет, честнее поля,
// которое есть и молчит, — гейт манифеста теперь скажет автору «такого поля
// нет» вместо тишины.
//
// Новая запись сюда — исключение, а не способ закрыть красную сборку. Если она
// звучит как «не построено» — значит, поле надо удалить, а не описывать.
var knownDeadFields = map[string]string{}

// stripComments убирает // … до конца строки и /* … */ целиком. Грубо (строки
// в кавычках не щадит), но для поиска обращений «.поле» этого достаточно.
func stripComments(src string) string {
	var b strings.Builder
	for _, line := range strings.Split(src, "\n") {
		if i := strings.Index(line, "//"); i >= 0 {
			line = line[:i]
		}
		b.WriteString(line)
		b.WriteByte('\n')
	}
	out := b.String()
	for {
		i := strings.Index(out, "/*")
		if i < 0 {
			break
		}
		j := strings.Index(out[i:], "*/")
		if j < 0 {
			out = out[:i]
			break
		}
		out = out[:i] + out[i+j+2:]
	}
	return out
}

func TestConfigFieldsAreActuallyRead(t *testing.T) {
	root := repoRoot(t)
	cfgPath := filepath.Join(root, filepath.FromSlash(
		"unity/Packages/com.lvn.engine/Runtime/Content/LvnUiConfig.cs"))
	raw, err := os.ReadFile(cfgPath)
	if err != nil {
		t.Fatalf("LvnUiConfig.cs: %v", err)
	}

	fields := map[string]bool{}
	for name := range declaredFields(stripComments(string(raw))) {
		if len(name) > 3 {
			fields[name] = true
		}
	}
	if len(fields) < 50 {
		t.Fatalf("в LvnUiConfig.cs нашлось всего %d полей — разбор сломался", len(fields))
	}

	// Кто угодно в рантайме, кроме самого файла описаний.
	read := map[string]bool{}
	for _, rel := range storageRoots { // те же три пакета рантайма
		dir := filepath.Join(root, rel)
		_ = filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			if strings.HasSuffix(path, "LvnUiConfig.cs") || strings.Contains(filepath.ToSlash(path), "/Tests/") {
				return nil
			}
			body, err := os.ReadFile(path)
			if err != nil {
				return nil
			}
			// Комментарии вырезаем: упоминание поля в пояснении соседнего
			// файла — не чтение. На этом страж уже один раз ошибся, засчитав
			// «→ WardrobeConfig.rarity_colors» из комментария LvnManifest за
			// живое использование, и пропустил мёртвую редкость предметов.
			text := stripComments(string(body))
			for f := range fields {
				if read[f] {
					continue
				}
				if strings.Contains(text, "."+f) {
					read[f] = true
				}
			}
			return nil
		})
	}

	// Оправдание, пережившее САМО ПОЛЕ: поле удалили, строчка осталась. Без
	// этой проверки список тихо копит записи про то, чего в движке нет, — и
	// перестаёт быть ответом на вопрос «почему я это написал, а ничего не
	// изменилось».
	var gone []string
	for f := range knownDeadFields {
		if !fields[f] {
			gone = append(gone, f)
		}
	}
	sort.Strings(gone)
	if len(gone) > 0 {
		t.Errorf("в knownDeadFields числятся поля, которых в LvnUiConfig нет (%d): %s\n"+
			"Уберите записи — они пережили сами поля.", len(gone), strings.Join(gone, ", "))
	}

	var dead, staleExcuses []string
	for f := range fields {
		if read[f] {
			if _, listed := knownDeadFields[f]; listed {
				staleExcuses = append(staleExcuses, f)
			}
			continue
		}
		if _, listed := knownDeadFields[f]; !listed {
			dead = append(dead, f)
		}
	}
	sort.Strings(dead)
	sort.Strings(staleExcuses)

	if len(dead) > 0 {
		t.Errorf("поля манифеста, которых не читает никто (%d):\n  %s\n\n"+
			"Автор их увидит, напишет и не получит ничего — без единой ошибки. "+
			"Либо читайте поле, либо впишите его в knownDeadFields с причиной.",
			len(dead), strings.Join(dead, "\n  "))
	}
	if len(staleExcuses) > 0 {
		t.Errorf("поля числятся мёртвыми, а их читают (%d): %s\n"+
			"Уберите их из knownDeadFields — оправдание пережило причину.",
			len(staleExcuses), strings.Join(staleExcuses, ", "))
	}
}
