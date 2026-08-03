package lvns

import "strings"
import "testing"

// Сетка — главный инструмент автора-нейросети: она пишет координаты вслепую,
// и цена ошибки в метрах выше, чем в клетках. Тест держит оба обещания сетки:
// клетки честно переводятся в метры, а столкновение называется вслух.
func TestGridCellsBecomeMetres(t *testing.T) {
	src := strings.Join([]string{
		"scene t",
		"grid 2",
		`o3d id=склеп shape=box pos="-3,0,6" size="3,3,3"`,
		`o3d id=роща shape=cone pos="0,0,9" count=10 area="14,6" gap=1.5`,
		"grid off",
		`o3d id=метрами shape=sphere pos="0,1,4"`,
	}, "\n")

	out, warns := expandGrid(src)
	if len(warns) != 0 {
		t.Fatalf("непрошеные предупреждения: %v", warns)
	}
	if !strings.Contains(out, `pos="-6,0,12"`) {
		t.Errorf("клетка (−3, 6) при клетке 2 м должна стать (−6, 12):\n%s", out)
	}
	if !strings.Contains(out, `area="28,12"`) || !strings.Contains(out, "gap=3") {
		t.Errorf("площадь и просвет тоже в клетках:\n%s", out)
	}
	if !strings.Contains(out, `size="3,3,3"`) {
		t.Errorf("РАЗМЕР остаётся в метрах — рост тела клетками не мерят:\n%s", out)
	}
	if !strings.Contains(out, `pos="0,1,4"`) {
		t.Errorf("после `grid off` координаты снова метры:\n%s", out)
	}
}

func TestGridReportsOccupiedCell(t *testing.T) {
	src := strings.Join([]string{
		"scene t",
		"grid 2",
		`o3d id=фонарь shape=cylinder pos="1,0,2"`,
		`o3d id=скамья shape=box pos="1.4,0,2.2"`,
	}, "\n")

	_, warns := expandGrid(src)
	if len(warns) != 1 {
		t.Fatalf("два тела в клетке (1, 2) — ждали одно предупреждение, получили %d: %v", len(warns), warns)
	}
	for _, want := range []string{"(1, 2)", "фонарь", "скамья"} {
		if !strings.Contains(warns[0], want) {
			t.Errorf("предупреждение должно называть %q: %s", want, warns[0])
		}
	}
}

// Дробная координата внутри клетки — это подстройка, а не второе тело в ней:
// иначе автор не сможет сдвинуть предмет на полметра без ложной тревоги.
func TestGridFractionStaysInSameCell(t *testing.T) {
	src := "scene t\ngrid 2\n" + `o3d id=а shape=box pos="1.5,0,2.25"` + "\n"
	out, warns := expandGrid(src)
	if len(warns) != 0 {
		t.Fatalf("одно тело не может занять клетку дважды: %v", warns)
	}
	if !strings.Contains(out, `pos="3,0,4.5"`) {
		t.Errorf("дробные клетки переводятся так же:\n%s", out)
	}
}

// Две ступени сетки: крупное меряется клетками, мелочь — подклетками. Тест
// держит именно это разделение: свеча рядом со свечой в одной клетке — норма,
// две свечи в одной подклетке — ошибка.
func TestSubGridSeparatesBigFromSmall(t *testing.T) {
	src := strings.Join([]string{
		"scene t",
		"grid 2 sub 10",
		`o3d id=надгробие shape=box pos="1,0,3" size="1.1,1.4,0.4"`,
		`o3d id=свеча_л shape=cylinder pos="1.2,0,3.2" size="0.08,0.22,0.08"`,
		`o3d id=свеча_п shape=cylinder pos="1.6,0,3.2" size="0.08,0.22,0.08"`,
	}, "\n")
	out, warns := expandGrid(src)
	if len(warns) != 0 {
		t.Fatalf("свечи в разных подклетках одной клетки — это норма: %v", warns)
	}
	// Подсетка 10 при клетке 2 м даёт шаг 20 см: 1.2 клетки → 2.4 м.
	if !strings.Contains(out, `pos="2.4,0,6.4"`) {
		t.Errorf("подклетка переводится тем же множителем:\n%s", out)
	}

	clash := strings.Join([]string{
		"scene t",
		"grid 2 sub 10",
		`o3d id=венок  shape=disc pos="1.4,0,3.6" size="0.5,1,0.5"`,
		`o3d id=монета shape=disc pos="1.4,0,3.6" size="0.06,1,0.06"`,
	}, "\n")
	_, warns = expandGrid(clash)
	if len(warns) != 1 || !strings.Contains(warns[0], "подклетка") {
		t.Fatalf("две мелочи в одной подклетке — ошибка: %v", warns)
	}
}

// Прилипание к подсетке: без него координаты копятся хвостами и проверка
// занятости перестаёт что-либо ловить.
func TestSubGridSnapsCoordinates(t *testing.T) {
	src := "scene t\ngrid 2 sub 10\n" + `o3d id=а shape=box pos="1.47,0,3.03" size="0.1,0.1,0.1"` + "\n"
	out, _ := expandGrid(src)
	if !strings.Contains(out, `pos="3,0,6"`) {
		t.Errorf("1.47 при подсетке 10 прилипает к 1.5 → 3 м:\n%s", out)
	}
}
