package lvns

// ПОГОДА — одно слово вместо десяти согласованных чисел.
//
// Главный урок больших движков про красивый кадр не в том, сколько в нём
// эффектов, а в том, что все они описывают ОДНО И ТО ЖЕ. Небо, солнце, туман,
// цвет тени и рассеянный свет — это не пять независимых настроек, а пять
// следствий одной погоды. Стоит рассогласовать их — и кадр разваливается:
// синее небо с тёплой тенью, туман цвета, которого нет на горизонте,
// закатное солнце при полуденном ambient.
//
// Автор (а чаще нейросеть) рассогласовывает их постоянно, и не по глупости:
// держать в голове десять чисел, связанных физикой, невозможно. Поэтому здесь
// он пишет
//
//	weather id=clear_sunset
//
// и получает целый световой мир. А если нужна поправка — правит ровно её:
//
//	weather id=clear_sunset fog=0.18
//
// Это конструкция ВРЕМЕНИ КОМПИЛЯЦИИ (правило цены из docs/adding-an-op.md):
// строка разворачивается в обычные `light` и `bg3d`, рантайм о погоде не знает
// и знать не должен. Цена — две реализации вместо шести, и погода работает в
// плеере, который о ней никогда не слышал.

import (
	"fmt"
	"regexp"
	"sort"
	"strings"
)

// preset — согласованный световой мир. Значения подобраны так, чтобы небо,
// солнце, туман и тень описывали одно состояние атмосферы.
type preset struct {
	// Небо: зенит, горизонт и цвет рассеянного света.
	skyTop, skyBottom, skyAmbient string
	skyPower                      float64
	// Солнце: угол (высота, азимут), цвет, сила.
	sunAngle, sunColor string
	sunPower           float64
	// Заполняющий — отражённый свет земли и неба с теневой стороны.
	fillColor string
	fillPower float64
	// Туман: цвет и дальность.
	fogColor        string
	fogNear, fogFar float64
	// Профиль стиля: тень, ободок, тёплая кайма.
	shadowTint, rimColor string
	rim, warm            float64
	// Тональная компрессия под эту погоду.
	exposure, white float64
	// Человеческое название — чтобы в предупреждении было видно, что выбрано.
	title string
}

// Пресеты. Названия — по состоянию неба, а не по времени суток: «ясный закат»
// и «пасмурный закат» освещают сцену совершенно по-разному, а «вечер» не
// говорит ни о том, ни о другом.
var weatherPresets = map[string]preset{
	"clear_noon": {
		title:     "ясный полдень",
		skyTop:    "#3f7fd0", skyBottom: "#bcd4ea", skyAmbient: "#8fb0cf", skyPower: 1.0,
		sunAngle: "58,-30", sunColor: "#fff6e2", sunPower: 1.25,
		fillColor: "#6f89a8", fillPower: 0.35,
		fogColor: "#c3d6e8", fogNear: 30, fogFar: 220,
		shadowTint: "#5a6f92", rimColor: "#cfe3ff", rim: 0.35, warm: 0.14,
		exposure: -0.2, white: 2.4,
	},
	"clear_sunset": {
		title:     "ясный закат",
		skyTop:    "#2b4a7e", skyBottom: "#f0a468", skyAmbient: "#8c7590", skyPower: 0.85,
		sunAngle: "8,-96", sunColor: "#ffb26b", sunPower: 1.1,
		fillColor: "#5d6f9c", fillPower: 0.32,
		fogColor: "#d9a37e", fogNear: 18, fogFar: 150,
		shadowTint: "#43506f", rimColor: "#ffd0a0", rim: 0.6, warm: 0.3,
		exposure: -0.1, white: 2.6,
	},
	"overcast": {
		title:     "пасмурно",
		skyTop:    "#8d99a6", skyBottom: "#b9c2ca", skyAmbient: "#9aa5b1", skyPower: 0.95,
		sunAngle: "45,-40", sunColor: "#c9cfd6", sunPower: 0.45,
		fillColor: "#8b96a2", fillPower: 0.5,
		fogColor: "#b5bec7", fogNear: 14, fogFar: 110,
		shadowTint: "#6b7683", rimColor: "#c8d2dc", rim: 0.2, warm: 0.05,
		exposure: 0.15, white: 1.9,
	},
	"night_clear": {
		title:     "ясная ночь",
		skyTop:    "#070c1c", skyBottom: "#1d2740", skyAmbient: "#16203a", skyPower: 0.55,
		sunAngle: "35,-42", sunColor: "#6a8bc9", sunPower: 0.45,
		fillColor: "#1b2340", fillPower: 0.14,
		fogColor: "#222d4a", fogNear: 10, fogFar: 55,
		shadowTint: "#141833", rimColor: "#7798d4", rim: 0.75, warm: 0.02,
		exposure: 0.35, white: 2.2,
	},
	"night_fog": {
		title:     "туманная ночь",
		skyTop:    "#0b1021", skyBottom: "#26314f", skyAmbient: "#18223d", skyPower: 0.6,
		sunAngle: "30,-50", sunColor: "#5d7ab0", sunPower: 0.35,
		fillColor: "#20284d", fillPower: 0.16,
		fogColor: "#26314f", fogNear: 6, fogFar: 34,
		shadowTint: "#141833", rimColor: "#7798d4", rim: 0.8, warm: 0.02,
		exposure: 0.45, white: 2.0,
	},
	"dawn": {
		title:     "рассвет",
		skyTop:    "#0f2038", skyBottom: "#93a7b8", skyAmbient: "#4a5f78", skyPower: 0.45,
		sunAngle: "6,-96", sunColor: "#ffd2a8", sunPower: 0.8,
		fillColor: "#5d7396", fillPower: 0.4,
		fogColor: "#9fb0c0", fogNear: 12, fogFar: 90,
		shadowTint: "#3d4c6b", rimColor: "#ffdcb8", rim: 0.5, warm: 0.22,
		exposure: 0.1, white: 2.3,
	},
	"storm": {
		title:     "гроза",
		skyTop:    "#232a35", skyBottom: "#4a545f", skyAmbient: "#39424e", skyPower: 0.7,
		sunAngle: "40,-55", sunColor: "#7f8b99", sunPower: 0.3,
		fillColor: "#39424e", fillPower: 0.4,
		fogColor: "#59636e", fogNear: 8, fogFar: 60,
		shadowTint: "#2b323d", rimColor: "#9aa7b5", rim: 0.3, warm: 0.04,
		exposure: 0.3, white: 1.8,
	},
}

var reWeather = regexp.MustCompile(`^\s*(?:weather|погода)\s+(.*)$`)

// expandWeather разворачивает `weather` в согласованные команды света.
func expandWeather(src string) (string, []string) {
	lines := strings.Split(src, "\n")
	out := make([]string, 0, len(lines)+8)
	var warns []string

	for i, line := range lines {
		m := reWeather.FindStringSubmatch(line)
		if m == nil {
			out = append(out, line)
			continue
		}
		rest := m[1]
		id := strings.ToLower(strings.Trim(fieldStr(rest, "id"), `"`))
		p, ok := weatherPresets[id]
		if !ok {
			warns = append(warns, fmt.Sprintf(
				"line %d: погода «%s» неизвестна — бывает: %s", i+1, id, weatherNames()))
			out = append(out, line)
			continue
		}

		// ТОЧЕЧНЫЕ ПОПРАВКИ. Пресет — это согласованные значения по умолчанию,
		// а не запрет их менять: автор всегда может сказать «то же самое, но
		// тумана вдвое больше». Поправка перекрывает ровно одно поле.
		if v, has := numField(rest, "sun"); has {
			p.sunPower = v
		}
		if v, has := numField(rest, "fog"); has {
			// Туман задаётся ПЛОТНОСТЬЮ, а не дальностью: «0.18» понятнее,
			// чем «дымка кончается на сорока двух метрах». Плотнее — ближе.
			p.fogFar = p.fogFar * (0.1 / clamp(v, 0.01, 1))
			p.fogNear = p.fogNear * (0.1 / clamp(v, 0.01, 1))
		}
		if v, has := numField(rest, "exposure"); has {
			p.exposure = v
		}
		if v, has := numField(rest, "rim"); has {
			p.rim = v
		}
		if c := fieldStr(rest, "fog_color"); c != "" {
			p.fogColor = strings.Trim(c, `"`)
		}

		out = append(out, fmt.Sprintf("// погода: %s", p.title))
		out = append(out, fmt.Sprintf(`light kind=sky top=%q bottom=%q color=%q power=%s`,
			p.skyTop, p.skyBottom, p.skyAmbient, trimFloat(p.skyPower)))
		out = append(out, fmt.Sprintf(`light kind=sun angle=%q color=%q power=%s`,
			p.sunAngle, p.sunColor, trimFloat(p.sunPower)))
		out = append(out, fmt.Sprintf(`light kind=fill color=%q power=%s`,
			p.fillColor, trimFloat(p.fillPower)))
		out = append(out, fmt.Sprintf(`light kind=fog color=%q near=%s far=%s`,
			p.fogColor, trimFloat(p.fogNear), trimFloat(p.fogFar)))
		// Профиль стиля и тональная компрессия — той же погодой: цвет тени
		// обязан быть цветом неба с теневой стороны, иначе кадр развалится
		// ровно так, как разваливался до появления этой команды.
		out = append(out, fmt.Sprintf(
			`bg3d shadow_tint=%q rim_color=%q rim=%s warm=%s tone=neutral exposure=%s white=%s`,
			p.shadowTint, p.rimColor, trimFloat(p.rim), trimFloat(p.warm),
			trimFloat(p.exposure), trimFloat(p.white)))
	}
	return strings.Join(out, "\n"), warns
}

func weatherNames() string {
	names := make([]string, 0, len(weatherPresets))
	for k := range weatherPresets {
		names = append(names, k)
	}
	sort.Strings(names)
	return strings.Join(names, ", ")
}

func clamp(v, lo, hi float64) float64 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// numField — число поля и признак того, что поле вообще было: ноль как
// значение и ноль как «не задано» — разные вещи (`sun=0` гасит солнце).
func numField(line, key string) (float64, bool) {
	if fieldStr(line, key) == "" {
		return 0, false
	}
	return fieldNum(line, key), true
}
