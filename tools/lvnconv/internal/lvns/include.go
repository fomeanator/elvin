package lvns

// include — подключение одного .lvns-файла в другой.
//
// Зачем: большая игра пишется главами, а таблицы, функции и пресеты постановки
// у них общие. Без include общий кусок копируется в каждую главу, и любая
// правка требует обойти все копии — то есть расходится ровно так же, как
// расходились шесть зеркал словаря опов.
//
// Это КОНСТРУКЦИЯ ВРЕМЕНИ КОМПИЛЯЦИИ (docs/adding-an-op.md): подстановка
// происходит до разбора, рантайм о ней не знает, новых опов не появляется.
// Поэтому цена — две реализации (этот компилятор и его C#-порт), а не шесть.
//
// Решения, которые стоило принять явно:
//
//   - путь резолвится относительно ПОДКЛЮЧАЮЩЕГО файла, а не рабочей
//     директории: иначе одна и та же глава компилируется по-разному из разных
//     мест, и это находят в самый неудобный момент;
//   - повторное подключение ИДЕМПОТЕНТНО (второй раз пропускается). Текстовая
//     подстановка дважды внесла бы дубли меток, а дубль метки — ошибка
//     валидатора. Ромб A→B, A→C, B→D, C→D обязан работать;
//   - цикл — ошибка с полной цепочкой файлов, а не переполнение стека;
//   - номера строк остаются честными: ведётся карта «строка склейки → файл и
//     строка в нём», и сообщение об ошибке переписывается на границе Convert.

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

// reInclude — директива подключения. Путь только в кавычках: без них пробел в
// имени файла разобрался бы как второй аргумент и ошибка была бы невнятной.
var reInclude = regexp.MustCompile(`^\s*include\s+"([^"]+)"\s*$`)

// srcRef — откуда пришла строка склеенного источника.
type srcRef struct {
	File string // путь как его написал автор (для сообщений), "" для корневого файла
	Line int    // номер строки в ЭТОМ файле, начиная с 1
}

// expandIncludes раскрывает директивы и возвращает склеенный источник вместе с
// картой происхождения каждой строки.
func expandIncludes(src, dir, display, rootAbs string) (string, []srcRef, error) {
	var out []string
	var refs []srcRef
	seen := map[string]bool{}
	// Корень в цепочке с самого начала: иначе a -> b -> a читалось бы как
	// "b -> a -> b", и автор не увидел бы, откуда всё началось.
	chain := []string{}
	if rootAbs != "" {
		chain = append(chain, rootAbs)
		seen[rootAbs] = true
	}
	if err := expandInto(&out, &refs, src, dir, display, seen, chain); err != nil {
		return "", nil, err
	}
	return strings.Join(out, "\n"), refs, nil
}

func expandInto(out *[]string, refs *[]srcRef, src, dir, display string, seen map[string]bool, chain []string) error {
	for i, line := range strings.Split(src, "\n") {
		m := reInclude.FindStringSubmatch(line)
		if m == nil {
			*out = append(*out, line)
			*refs = append(*refs, srcRef{File: display, Line: i + 1})
			continue
		}
		rel := m[1]
		abs := filepath.Clean(filepath.Join(dir, filepath.FromSlash(rel)))

		for _, c := range chain {
			if c == abs {
				return fmt.Errorf("%s: include cycle: %s",
					at(display, i+1), strings.Join(shortChain(append(chain, abs)), " -> "))
			}
		}
		if seen[abs] {
			// Уже подключён выше. Молча пропускаем — это не ошибка автора, а
			// нормальная форма: два файла подключают одну общую механику.
			*out = append(*out, "// include "+rel+" (уже подключён)")
			*refs = append(*refs, srcRef{File: display, Line: i + 1})
			continue
		}
		data, err := os.ReadFile(abs)
		if err != nil {
			return fmt.Errorf("%s: include %q: файл не найден (искал %s)",
				at(display, i+1), rel, abs)
		}
		seen[abs] = true
		if err := expandInto(out, refs, string(data), filepath.Dir(abs), rel,
			seen, append(chain, abs)); err != nil {
			return err
		}
	}
	return nil
}

func at(display string, line int) string {
	if display == "" {
		return fmt.Sprintf("line %d", line)
	}
	return fmt.Sprintf("%s:%d", display, line)
}

func shortChain(paths []string) []string {
	out := make([]string, 0, len(paths))
	for _, p := range paths {
		out = append(out, filepath.Base(p))
	}
	return out
}

// reLinePrefix ловит "line N:" в начале ошибки компилятора. Все 35 мест в
// convert.go форматируют позицию именно так, поэтому переписать её достаточно
// ОДИН раз здесь, а не тащить файл через весь разбор.
var reLinePrefix = regexp.MustCompile(`^line (\d+):`)

// remapError переводит номер строки склейки в «файл:строка». Без этого автор,
// получив "line 412", идёт искать её в своей главе на 60 строк.
func remapError(err error, refs []srcRef) error {
	if err == nil {
		return nil
	}
	msg := err.Error()
	m := reLinePrefix.FindStringSubmatch(msg)
	if m == nil {
		return err
	}
	n, cerr := strconv.Atoi(m[1])
	if cerr != nil || n < 1 || n > len(refs) {
		return err
	}
	r := refs[n-1]
	// Перекладывать надо ВСЕГДА, а не только для подключённых файлов: строки
	// самого корневого файла после include тоже сдвинуты на длину подстановки.
	// Первая версия этого не делала, и ошибка в главе на строке 10 приезжала
	// как "line 66" — ровно та потеря ориентации, против которой всё затевалось.
	if r.File == "" {
		return fmt.Errorf("line %d:%s", r.Line, strings.TrimPrefix(msg, m[0]))
	}
	return fmt.Errorf("%s:%d:%s", r.File, r.Line, strings.TrimPrefix(msg, m[0]))
}

// ConvertFile компилирует .lvns с диска. Это единственный вход, умеющий
// include: подстановка требует знать, ОТНОСИТЕЛЬНО ЧЕГО резолвить путь, а
// Convert принимает только текст.
func ConvertFile(path string) (*Doc, error) {
	src, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	abs, _ := filepath.Abs(path)
	joined, refs, err := expandIncludes(string(src), filepath.Dir(path), "", filepath.Clean(abs))
	if err != nil {
		return nil, err
	}
	doc, err := Convert(joined)
	return doc, remapError(err, refs)
}

// strayInclude ловит директиву, дожившую до разбора. Так бывает при вызове
// Convert напрямую (без файла) — и без этой проверки строка провалилась бы в
// ветку наррации и НАПЕЧАТАЛАСЬ БЫ ИГРОКУ как реплика. Ровно эта ловушка
// описана седьмым шагом в docs/adding-an-op.md как единственная без стража.
func strayInclude(lines []string) error {
	for i, l := range lines {
		if m := reInclude.FindStringSubmatch(l); m != nil {
			return fmt.Errorf("line %d: include %q: подключение работает только при компиляции файла "+
				"(lvnconv convert -i …), а не текста без пути", i+1, m[1])
		}
	}
	return nil
}
