package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"
)

// ХРАПОВИК ШКАЛЫ КЕГЛЯ: числу размеров мимо ступеней позволено только убывать.
//
// У темы есть шкала (Xs 20, Sm 24, Base 30, Lg 38, Xl 48, Display 64) и при ней
// объяснение: «ШКАЛА, а не свободные числа: одинаковые вещи на разных экранах
// обязаны быть одного размера, иначе интерфейс расползается — что и произошло,
// когда каждый экран выбирал кегль на глаз».
//
// Расползание УЖЕ произошло: на момент установки стража в коде 29 разных
// кеглей, из них мимо шкалы 120 мест. Между 20 и 30 живут шесть самодельных
// ступеней (21, 22, 25, 26, 27, 28) — глазом такая разница неразличима, а
// сложенная по экрану даёт ту самую «расползлось».
//
// Свести всё разом нельзя: это меняет ВИД, а вид согласован отдельно
// (docs/visual-standards.md). Поэтому страж не требует чистоты — он не даёт
// стать хуже. Порог опускается по мере сведения; поднимать его нельзя.
func TestTypeScaleDoesNotSpreadFurther(t *testing.T) {
	const budget = 120 // мест мимо шкалы на 28.08.2026; только вниз

	root := repoRoot(t)
	scale := map[int]bool{20: true, 24: true, 30: true, 38: true, 48: true, 64: true}
	re := regexp.MustCompile(`style\.fontSize\s*=\s*(\d+)`)

	offScale := 0
	scanned := 0
	novel := map[int]int{}
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			scanned++
			for _, line := range strings.Split(string(b), "\n") {
				code := line
				if c := strings.Index(code, "//"); c >= 0 {
					code = code[:c]
				}
				for _, m := range re.FindAllStringSubmatch(code, -1) {
					n, _ := strconv.Atoi(m[1])
					if !scale[n] {
						offScale++
						novel[n]++
					}
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	// Порог охвата: «мимо шкалы ноль» и «обход сломался» дают одну и ту же
	// зелёную строку, а значат прямо противоположное.
	atLeast(t, scanned, 150, "просмотренных файлов")

	if offScale > budget {
		var sizes []int
		for n := range novel {
			sizes = append(sizes, n)
		}
		sort.Ints(sizes)
		var parts []string
		for _, n := range sizes {
			parts = append(parts, strconv.Itoa(n)+"×"+strconv.Itoa(novel[n]))
		}
		t.Fatalf("кеглей мимо шкалы стало %d при пороге %d.\nВстречаются: %s\n\n"+
			"Возьмите ближайшую ступень (20/24/30/38/48/64) или добавьте ступень в тему"+
			" осознанно — но не заводите ещё один размер «на глаз».",
			offScale, budget, strings.Join(parts, ", "))
	}
	if offScale < budget {
		t.Logf("мимо шкалы %d при пороге %d — порог можно опустить", offScale, budget)
	}
}

// ХРАПОВИК ШКАЛЫ ОТСТУПА — та же болезнь, что у кегля, и та же мера.
//
// В теме есть ступени (8, 12, 18, 26, 40, 60) и при них объяснение: «„на глаз“
// даёт 14, 15, 18 в соседних местах, и взгляд цепляется за разнобой». Отступов,
// поставленных числом, — 708; мимо ступеней 448, и самая частая самоделка это
// как раз 14 (80 мест) и 10 (99). То есть комментарий описывает не опасение, а
// уже случившееся.
//
// Ноль не считается: «убрать отступ» — не ступень шкалы, а его отсутствие.
func TestSpaceScaleDoesNotSpreadFurther(t *testing.T) {
	const budget = 448 // мест мимо шкалы на 28.08.2026; только вниз

	root := repoRoot(t)
	scale := map[int]bool{8: true, 12: true, 18: true, 26: true, 40: true, 60: true}
	re := regexp.MustCompile(`style\.(?:padding|margin)\w*\s*=\s*(\d+)`)

	off := 0
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for _, line := range strings.Split(string(b), "\n") {
				code := line
				if c := strings.Index(code, "//"); c >= 0 {
					code = code[:c]
				}
				for _, m := range re.FindAllStringSubmatch(code, -1) {
					n, _ := strconv.Atoi(m[1])
					if n != 0 && !scale[n] {
						off++
					}
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}
	if off > budget {
		t.Fatalf("отступов мимо шкалы стало %d при пороге %d.\n\n"+
			"Возьмите ступень (8/12/18/26/40/60) или добавьте ступень в тему осознанно:"+
			" разнобой в 2 пункта не виден на правке и виден на экране.", off, budget)
	}
}
