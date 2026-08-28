package lvn

import (
	"encoding/binary"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// Гарнитура из каталога обязана уметь кириллицу — и лежать в пакете.
//
// Шрифт без кириллицы в русской новелле показывает не текст, а квадраты, и
// увидит это игрок, а не автор: в редакторе на английской заглушке всё
// прекрасно. Второй тихий отказ — путь, по которому файла нет: Resources.Load
// вернёт null, и текст молча останется прежним, то есть настройка «не
// работает» без единой строки в логе.
//
// Поэтому каждая строка каталога LvnFonts.Families проверяется по файлу: он
// существует и в его cmap есть русские буквы.
func TestFontFamiliesCoverCyrillic(t *testing.T) {
	root := repoRoot(t)
	src := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "UI", "LvnFonts.cs")
	b, err := os.ReadFile(src)
	if err != nil {
		t.Fatalf("дом шрифтов не найден: %v", err)
	}

	// new Family("id", "Название", "Fonts/File", "Fonts/DisplayFile")
	re := regexp.MustCompile(`new Family\("([^"]+)",\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)"\)`)
	rows := re.FindAllStringSubmatch(string(b), -1)
	if len(rows) == 0 {
		t.Fatal("в каталоге LvnFonts.Families не разобрана ни одна гарнитура")
	}

	fontsDir := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "Resources")
	var problems []string
	seen := map[string]bool{}

	for _, r := range rows {
		id, title := r[1], r[2]
		if seen[id] {
			problems = append(problems, fmt.Sprintf("%s: ключ повторяется", id))
		}
		seen[id] = true
		for _, p := range []string{r[3], r[4]} {
			path := filepath.Join(fontsDir, filepath.FromSlash(p)+".ttf")
			raw, err := os.ReadFile(path)
			if err != nil {
				problems = append(problems, fmt.Sprintf("%s (%s): файла нет — %s", title, id, p))
				continue
			}
			if !fontHasCyrillic(raw) {
				problems = append(problems,
					fmt.Sprintf("%s (%s): в шрифте нет кириллицы — игрок увидит квадраты", title, id))
			}
		}
	}

	sort.Strings(problems)
	if len(problems) > 0 {
		t.Fatalf("каталог гарнитур не годится:\n  %s", strings.Join(problems, "\n  "))
	}
}

// Есть ли в ШРИФТЕ русские буквы: ищем «А» (U+0410) и «я» (U+044F) в cmap.
func fontHasCyrillic(b []byte) bool {
	return hasGlyph(b, 0x0410) && hasGlyph(b, 0x044F)
}

func hasGlyph(b []byte, want uint32) bool {
	if len(b) < 12 {
		return false
	}
	num := int(binary.BigEndian.Uint16(b[4:6]))
	cmapOff := -1
	for i := 0; i < num; i++ {
		rec := 12 + i*16
		if rec+16 > len(b) {
			return false
		}
		if string(b[rec:rec+4]) == "cmap" {
			cmapOff = int(binary.BigEndian.Uint32(b[rec+8 : rec+12]))
		}
	}
	if cmapOff < 0 || cmapOff+4 > len(b) {
		return false
	}
	subs := int(binary.BigEndian.Uint16(b[cmapOff+2 : cmapOff+4]))
	for i := 0; i < subs; i++ {
		rec := cmapOff + 4 + i*8
		if rec+8 > len(b) {
			return false
		}
		off := cmapOff + int(binary.BigEndian.Uint32(b[rec+4:rec+8]))
		if off+4 > len(b) {
			continue
		}
		switch binary.BigEndian.Uint16(b[off : off+2]) {
		case 4:
			if lookup4(b, off, want) {
				return true
			}
		case 12:
			if lookup12(b, off, want) {
				return true
			}
		}
	}
	return false
}

func lookup4(b []byte, off int, want uint32) bool {
	if want > 0xFFFF || off+14 > len(b) {
		return false
	}
	segX2 := int(binary.BigEndian.Uint16(b[off+6 : off+8]))
	ends := off + 14
	starts := ends + segX2 + 2
	if starts+segX2 > len(b) {
		return false
	}
	for i := 0; i < segX2; i += 2 {
		end := uint32(binary.BigEndian.Uint16(b[ends+i : ends+i+2]))
		start := uint32(binary.BigEndian.Uint16(b[starts+i : starts+i+2]))
		if want >= start && want <= end && end != 0xFFFF {
			return true
		}
	}
	return false
}

func lookup12(b []byte, off int, want uint32) bool {
	if off+16 > len(b) {
		return false
	}
	groups := int(binary.BigEndian.Uint32(b[off+12 : off+16]))
	for i := 0; i < groups; i++ {
		g := off + 16 + i*12
		if g+12 > len(b) {
			return false
		}
		start := binary.BigEndian.Uint32(b[g : g+4])
		end := binary.BigEndian.Uint32(b[g+4 : g+8])
		if want >= start && want <= end {
			return true
		}
	}
	return false
}
