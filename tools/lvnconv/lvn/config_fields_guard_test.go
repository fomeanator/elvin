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
var csField = regexp.MustCompile(`(?m)^\s*public\s+[\w?<>\[\], .]+\s+(\w+)\s*;`)

// Поля, которых сегодня не читает никто, и почему это осознанно.
var knownDeadFields = map[string]string{
	// Наследие прежнего дизайна гардероба: отдельных кнопок «Надеть / Надето /
	// Снять» в листе больше нет — есть примерка и одно подтверждение.
	"equip_text":     "прежний дизайн гардероба: кнопок «надеть/снять» в листе нет",
	"equipped_text":  "прежний дизайн гардероба: состояние показывает рамка, не подпись",
	"remove_text":    "прежний дизайн гардероба: снятие идёт значением «нет», не кнопкой",
	"more_text":      "прежний дизайн витрины: карточка открывает деталь целиком, кнопки «подробнее» нет",
	"section_titles": "прежний дизайн витрины: заголовки секций приходят из collections",

	// Оформление, которое экраны берут из темы, а не из своего блока.
	"section_title_color": "цвет заголовка секции идёт из темы (LvnTokens)",
	"preview_bg_color":    "фон превью гардероба идёт из темы",
	"preview_bg_image":    "фон превью гардероба идёт из темы",
	"buy_color":           "цвет кнопки покупки идёт из темы",
	"buy_text_color":      "цвет текста кнопки покупки идёт из темы",

	// Задуманное, но не построенное — названо, чтобы не выглядело работающим.
	"pay_banner_always":     "баннер оплаты не построен",
	"pay_banner_color":      "баннер оплаты не построен",
	"pay_banner_text":       "баннер оплаты не построен",
	"pay_banner_text_color": "баннер оплаты не построен",
	"pay_banner_url":        "баннер оплаты не построен",
	"ad_text":               "подпись кнопки рекламы приходит из места вызова",
	"badge_url":             "бейдж формы ввода рисуется подписью, не картинкой",
	"hero_url":              "арт героини на форме ввода не построен",
	"bonus_text":            "текст бонуса берётся из словаря (shop.bonus)",
	"original_lang_text":    "подпись исходного языка приходит из словаря",
	"doll_x":                "горизонталь куклы считается по месту створа",
	"title_label":           "подпись створа приходит из ui.portal.title_label",
}

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
	for _, m := range csField.FindAllStringSubmatch(string(raw), -1) {
		if len(m[1]) > 3 {
			fields[m[1]] = true
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
