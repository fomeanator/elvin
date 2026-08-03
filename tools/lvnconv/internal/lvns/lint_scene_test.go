package lvns

import (
	"strings"
	"testing"
)

// Каждый случай здесь — «скрипт правильный, а кадр пустой». Это тот класс
// ошибок, который автор-нейросеть не может увидеть сама: экрана она не видит,
// а компилятор до сегодняшнего дня молчал.
func TestSceneLintCatchesInvisibleMistakes(t *testing.T) {
	cases := []struct {
		name string
		src  string
		want string
	}{
		{
			"опечатка в имени команды печатается игроку",
			"scene t\nо3d id=камень shape=box pos=\"1,0,2\"\n",
			"НАПЕЧАТАЕТСЯ ИГРОКУ",
		},
		{
			"второй o3d с тем же id двигает тело, а не добавляет новое",
			"scene t\n" +
				`o3d id=камень shape=box pos="1,0,2" size="1,1,1"` + "\n" +
				`o3d id=камень shape=box pos="3,0,4" size="1,1,1"` + "\n",
			"ПЕРЕДВИНЕТ",
		},
		{
			"тело за спиной камеры",
			"scene t\n" + `bg3d build=1 x=0 y=1.6 z=-5` + "\n" +
				`o3d id=шар shape=sphere pos="0,0,-9" size="1,1,1"` + "\n",
			"ПОЗАДИ камеры",
		},
		{
			"тело за границей тумана",
			"scene t\n" + `bg3d build=1 x=0 y=1.6 z=-5` + "\n" +
				`light kind=fog near=6 far=25` + "\n" +
				`o3d id=ель shape=cone pos="0,0,60" size="4,8,4"` + "\n",
			"туман смыкается",
		},
		{
			"сцена без света",
			"scene t\n" + `o3d id=камень shape=box pos="1,0,2" size="1,1,1"` + "\n",
			"кадр будет чёрным",
		},
		{
			"размер в сантиметрах вместо метров",
			"scene t\n" + `o3d id=пыль shape=sphere pos="1,0,2" size="0.005,0.005,0.005"` + "\n",
			"задаётся в МЕТРАХ",
		},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			warns := LintScene(c.src)
			for _, w := range warns {
				if strings.Contains(w, c.want) {
					return
				}
			}
			t.Errorf("ждали предупреждение про %q, получили: %v", c.want, warns)
		})
	}
}

// Обратная сторона: исправная сцена не должна поднимать ложных тревог, иначе
// предупреждения перестанут читать — и настоящие утонут в шуме.
func TestSceneLintStaysQuietOnGoodScene(t *testing.T) {
	src := strings.Join([]string{
		"scene t",
		`bg3d build=1 x=0 y=1.7 z=-6 pitch=3 fov=52`,
		`light kind=sun angle="38,-30" color="#ffeccd" power=0.9`,
		`light kind=fill color="#4a6ea0" power=0.35`,
		`light kind=fog color="#213040" near=12 far=60`,
		`o3d id=земля shape=plane pos="0,0,0" size="80,1,80" color="#3a4636"`,
		`o3d id=камень shape=box pos="2,0,5" size="1,0.6,1" color="#6b7078"`,
		`o3d id=ель shape=cone pos="-4,0,12" size="2.4,6,2.4" color="#1d3526"`,
		"Обычная сцена: свет есть, всё в кадре, имена разные.",
	}, "\n")

	if warns := LintScene(src); len(warns) != 0 {
		t.Errorf("исправная сцена должна проходить молча, получили: %v", warns)
	}
}

// Реплика с равенством внутри — не команда: «Аня: правда=ложь» законна и
// печататься должна как есть.
func TestSceneLintDoesNotTouchDialogue(t *testing.T) {
	src := "scene t\nАня: правда=ложь, а ложь=правда\n"
	if warns := LintScene(src); len(warns) != 0 {
		t.Errorf("реплика — не команда: %v", warns)
	}
}

// Опечатка в имени модели — тихий отказ высшей пробы: тело не встаёт, в логе
// ни слова, и автор ищет причину в свете и камере. Подсказка с ближайшим
// именем важнее самого предупреждения: состава набора автор не видит.
func TestSceneLintCatchesUnknownModel(t *testing.T) {
	SetModels["graveyard"] = []string{"crypt-a", "crypt-b", "gravestone-cross", "bench"}
	defer delete(SetModels, "graveyard")

	src := strings.Join([]string{
		"scene t",
		`bg3d id=graveyard x=0 y=1.6 z=-5`,
		`light kind=sun power=0.8`,
		`o3d id=склеп model=crypt-x pos="0,0,6" size="3,3,3"`,
	}, "\n")

	warns := LintScene(src)
	if len(warns) == 0 {
		t.Fatal("опечатка в имени модели должна быть названа")
	}
	if !strings.Contains(warns[0], "crypt-x") || !strings.Contains(warns[0], "crypt-a") {
		t.Errorf("нужно и назвать ошибку, и подсказать ближайшее имя: %s", warns[0])
	}

	// Известная модель и примитив тревоги не поднимают.
	ok := strings.Join([]string{
		"scene t",
		`bg3d id=graveyard x=0 y=1.6 z=-5`,
		`light kind=sun power=0.8`,
		`o3d id=склеп model=crypt-a pos="0,0,6" size="3,3,3"`,
		`o3d id=камень shape=box pos="2,0,6" size="1,1,1" color="#6b7078"`,
	}, "\n")
	if w := LintScene(ok); len(w) != 0 {
		t.Errorf("исправная сцена должна проходить молча: %v", w)
	}
}

// Цвет словом — молчаливая потеря: тело остаётся белым.
func TestSceneLintCatchesWordColour(t *testing.T) {
	src := "scene t\n" + `bg3d build=1 x=0 y=1.6 z=-5` + "\n" +
		`light kind=sun power=1` + "\n" +
		`o3d id=а shape=box pos="0,0,3" size="1,1,1" color=red` + "\n"
	warns := LintScene(src)
	found := false
	for _, w := range warns {
		if strings.Contains(w, "#rrggbb") {
			found = true
		}
	}
	if !found {
		t.Errorf("«red» — не цвет для движка: %v", warns)
	}
}

// Отношения — то, чем автор думает о сцене на самом деле: «скамья у фонаря»,
// а не «скамья в 0.6 метра». Тест держит арифметику привязок.
func TestRelationsResolveToCoordinates(t *testing.T) {
	src := strings.Join([]string{
		"scene t",
		`o3d id=фонарь shape=cylinder pos="2,0,5" size="0.3,2.4,0.3"`,
		`o3d id=скамья shape=box near=фонарь dist=1.4 side=left size="1.6,0.5,0.6"`,
		`o3d id=урна shape=cylinder near=фонарь side=right size="0.4,0.7,0.4"`,
		`o3d id=надгробие shape=box pos="-2,0,6" size="1,1.2,0.4"`,
		`o3d id=свеча shape=cylinder on=надгробие size="0.08,0.2,0.08"`,
	}, "\n")

	out, warns := expandRelations(src)
	if len(warns) != 0 {
		t.Fatalf("привязки к уже поставленным телам не должны ругаться: %v", warns)
	}
	for _, want := range []string{
		`pos="0.6,0,5"`,   // слева от фонаря на 1.4 м
		`pos="2.65,0,5"`,  // справа: половина габарита опоры + полметра
		`pos="-2,1.2,6"`,  // на надгробии: поднято на его высоту
	} {
		if !strings.Contains(out, want) {
			t.Errorf("ждали %s в результате:\n%s", want, out)
		}
	}
	// Поля отношений не должны доехать до рантайма — он их не знает.
	for _, gone := range []string{"near=", "on=", "dist=", "side="} {
		if strings.Contains(out, gone) {
			t.Errorf("поле %s должно исчезнуть после разворачивания:\n%s", gone, out)
		}
	}
}

// Привязка к телу, которого ещё нет, — самая частая ошибка порядка: автор
// пишет сцену сверху вниз и ссылается вперёд.
func TestRelationsRejectForwardReference(t *testing.T) {
	src := "scene t\n" + `o3d id=скамья shape=box near=фонарь side=left` + "\n" +
		`o3d id=фонарь shape=cylinder pos="2,0,5"` + "\n"
	_, warns := expandRelations(src)
	if len(warns) != 1 || !strings.Contains(warns[0], "которого ещё нет") {
		t.Fatalf("ссылка вперёд должна быть названа: %v", warns)
	}
}

// Достижение — состояние игрока, а не команда сцены: оно должно разворачиваться
// в обычную межновелльную переменную, которая и так персистится.
func TestAchievementsBecomeGlobalState(t *testing.T) {
	src := strings.Join([]string{
		"scene t",
		`achieve первая_кровь "Первая кровь"`,
		`achieve без_урона "Без единого удара" "Пройти главу, не получив урона"`,
	}, "\n")

	out, warns := expandAchievements(src)
	if len(warns) != 0 {
		t.Fatalf("разные достижения не должны ругаться: %v", warns)
	}
	for _, want := range []string{
		`set global.ach_первая_кровь = "Первая кровь"`,
		`set global.ach_без_урона = "Без единого удара"`,
		`set global.achd_без_урона = "Пройти главу, не получив урона"`,
	} {
		if !strings.Contains(out, want) {
			t.Errorf("ждали строку %q:\n%s", want, out)
		}
	}
}

// Одно достижение из двух веток сюжета — норма, но два РАЗНЫХ под одним
// ключом почти наверняка опечатка в идентификаторе.
func TestAchievementsWarnOnRepeat(t *testing.T) {
	src := "scene t\n" + `achieve шаг "Первый шаг"` + "\n" + `achieve шаг "Второй шаг"` + "\n"
	_, warns := expandAchievements(src)
	if len(warns) != 1 {
		t.Fatalf("повтор идентификатора должен быть назван: %v", warns)
	}
}

// Примитив без цвета выходит белым — честно, но почти никогда не замысел.
func TestSceneLintWarnsColourlessBody(t *testing.T) {
	src := "scene t\n" + `bg3d build=1 x=0 y=1.6 z=-5` + "\n" +
		`light kind=sun power=1` + "\n" +
		`o3d id=ель shape=cone pos="0,0,6" size="2,6,2"` + "\n"
	found := false
	for _, w := range LintScene(src) {
		if strings.Contains(w, "будет БЕЛЫМ") {
			found = true
		}
	}
	if !found {
		t.Error("тело без цвета и текстуры должно быть названо")
	}

	// Огонь красит себя сам — ему цвет не нужен, и тревожить не за что.
	fire := "scene t\n" + `bg3d build=1 x=0 y=1.6 z=-5` + "\n" +
		`light kind=sun power=1` + "\n" +
		`o3d id=пламя shape=cone pos="0,0,6" size="0.5,1,0.5" shader=fire` + "\n"
	for _, w := range LintScene(fire) {
		if strings.Contains(w, "БЕЛЫМ") {
			t.Errorf("огонь красит себя сам: %s", w)
		}
	}
}

// Границы правдоподобия ловят ПОРЯДОК ошибки — сантиметры вместо метров,
// множитель «на всякий случай», значение из чужого движка.
func TestLimitsCatchImplausibleNumbers(t *testing.T) {
	cases := []struct{ line, want string }{
		{`bg3d build=1 x=0 y=1.6 z=-5 fov=140`, "рыбьего глаза"},
		{`light kind=sun power=25`, "выжигает кадр"},
		{`o3d id=ель shape=cone pos="0,0,6" size="2,6,2" color="#1d3526" wind=8`, "буря"},
		{`o3d id=пыль shape=sphere pos="2,0,6" size="0.5,0.5,0.5" color="#888888" alpha=3`, "доля от нуля"},
		{`o3d id=диск shape=disc pos="0,0,4" size="1,1,1" color="#888888" scale_var=5`, "схлопывает"},
	}
	for _, c := range cases {
		src := "scene t\n" + `light kind=sun power=1` + "\n" + c.line + "\n"
		found := false
		for _, w := range LintScene(src) {
			if strings.Contains(w, c.want) {
				found = true
			}
		}
		if !found {
			t.Errorf("строка %q должна была вызвать предупреждение про %q; получили: %v",
				c.line, c.want, LintScene(src))
		}
	}
}

// «// lvn-ok» — выход для автора, которому нужен нарочно странный кадр. Без
// него линтер становится врагом: ложная тревога в каждой сборке приучает не
// читать предупреждения, и настоящие тонут.
func TestLintOkSuppressesLine(t *testing.T) {
	src := "scene t\n" + `light kind=sun power=1` + "\n" +
		`bg3d build=1 x=0 y=1.6 z=-5 fov=8 // lvn-ok: подзорная труба` + "\n"
	if w := LintScene(src); len(w) != 0 {
		t.Errorf("строка с «// lvn-ok» проверок не проходит: %v", w)
	}

	// Но соседние строки проверяются как обычно — подавление действует на одну.
	src2 := "scene t\n" + `light kind=sun power=1` + "\n" +
		`bg3d build=1 x=0 y=1.6 z=-5 fov=8 // lvn-ok` + "\n" +
		`o3d id=ель shape=cone pos="0,0,6" size="2,6,2" color="#1d3526" wind=9` + "\n"
	found := false
	for _, w := range LintScene(src2) {
		if strings.Contains(w, "буря") {
			found = true
		}
	}
	if !found {
		t.Error("подавление должно действовать только на свою строку")
	}
}
