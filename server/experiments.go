package main

// Эксперименты: кому какой вариант и почему именно ему.
//
// РАЗДЕЛЕНИЕ, ОПРЕДЕЛЯЮЩЕЕ ВСЁ ОСТАЛЬНОЕ. Развилка живёт в сценарии — только
// автор знает, где история может разойтись. Доля трафика, таргет и выключатель
// живут ЗДЕСЬ, на сервере. Запиши проценты в .lvns — и поменять 10% на 50%
// или остановить плохой вариант станет невозможно без новой сборки, то есть
// без недели ожидания, пока игроки обновятся. Эксперимент, который нельзя
// выключить сегодня, — это не эксперимент, а релиз.
//
// РЕШАЕТ СЕРВЕР, А НЕ КЛИЕНТ. Клиент не знает достоверно, откуда пришёл игрок:
// он видит ссылку запуска, а первое касание хранится у нас и неизменяемо.
// Поэтому таргет «только пришедшим с кампании X» возможен лишь на сервере — и
// заодно тем же словарём становятся доступны когорта и «платящий».
//
// СЛОИ. Если крутить несколько тестов сразу, игроки попадают в случайные
// комбинации и эффекты смешиваются: рост покупок от нового магазина
// неотличим от падения из-за переписанной сцены. Внутри одного слоя
// эксперименты ВЗАИМОИСКЛЮЧАЮЩИЕ — игрок участвует ровно в одном. Разные слои
// независимы по построению, и это ответственность того, кто их называет:
// «экономика» и «сюжет» пересекаться не должны по смыслу.
//
// ВЕСА НЕЛЬЗЯ МЕНЯТЬ МОЛЧА. Назначение — это хеш от имени и игрока, без
// хранения: так оно переживает переустановку и не требует круга к серверу.
// Но смена весов переставляет часть игроков в другие группы, и данные до и
// после становятся несравнимыми. Поэтому у эксперимента есть версия, она
// входит в хеш, и отчёт считает каждую версию отдельным экспериментом.
// Молчаливая переустановка людей — худший вид порчи данных: цифры выглядят
// нормально и означают не то.

import (
	"crypto/md5"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"net/http"
	"sort"
	"strings"
)

// errf — короткая ошибка с подстановкой; сообщения читает человек в панели,
// поэтому они на русском и объясняют последствие, а не только факт.
func errf(format string, a ...any) error { return fmt.Errorf(format, a...) }

// experimentVariant — вариант и его доля. Доли произвольные числа: сервер
// нормирует их сам, чтобы «70 и 30» и «7 и 3» значили одно и то же.
type experimentVariant struct {
	ID     string  `json:"id"`
	Weight float64 `json:"weight"`
}

// experimentAudience — кому эксперимент вообще показывается. Пустое поле
// означает «всем», а не «никому».
type experimentAudience struct {
	Channel string `json:"channel,omitempty"` // канал привлечения (первое касание)
	Cohort  string `json:"cohort,omitempty"`  // день первого прихода
	Payer   string `json:"payer,omitempty"`   // yes | no
}

type experiment struct {
	Name     string              `json:"name"`
	Variants []experimentVariant `json:"variants"`
	// Layer — слой взаимного исключения. Пусто = свой собственный слой, то
	// есть эксперимент ни с кем не конфликтует.
	Layer string `json:"layer,omitempty"`
	// Version — печать набора весов. Меняете доли — обязаны поднять версию,
	// иначе часть игроков переедет между группами, а отчёт этого не заметит.
	Version int `json:"version,omitempty"`
	// Enabled — выключатель. Выключенный эксперимент отдаёт первый вариант
	// всем: история продолжает работать, разделение прекращается.
	Enabled  bool               `json:"enabled"`
	Audience experimentAudience `json:"audience,omitempty"`
	Note     string             `json:"note,omitempty"`
}

type ExperimentsService struct {
	cfg        *hotJSON[[]experiment]
	auth       *AuthService
	payments   paymentsSource
	analytics  *AnalyticsService
	adminToken string
}

func NewExperimentsService(path string, auth *AuthService, adminToken string) *ExperimentsService {
	return &ExperimentsService{
		cfg:        newHotJSON(path, []experiment{}),
		auth:       auth,
		adminToken: adminToken,
	}
}

func (s *ExperimentsService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/experiments", s.handleMine)
	mux.HandleFunc("/v1/admin/experiments", s.handleAdmin)
}

// bucket — доля в [0,1), в которую попадает игрок. Хеш, а не жребий: жребий
// пришлось бы хранить, а хранимое теряется вместе с устройством.
func bucket(seed string) float64 {
	h := md5.Sum([]byte(seed))
	return float64(binary.BigEndian.Uint32(h[:4])) / float64(1<<32)
}

// pick выбирает вариант по весам. Возвращает пустую строку, если вариантов нет.
func (e experiment) pick(userID string) string {
	if len(e.Variants) == 0 {
		return ""
	}
	if !e.Enabled {
		// Выключенный эксперимент — это не «никакой группы», а «все в
		// первом варианте»: сцена обязана продолжать играться.
		return e.Variants[0].ID
	}
	total := 0.0
	for _, v := range e.Variants {
		if v.Weight > 0 {
			total += v.Weight
		}
	}
	if total <= 0 {
		// Веса не заданы — делим поровну. Это разумное умолчание: «просто
		// раздели пополам» самый частый случай.
		i := int(bucket(e.seed(userID)) * float64(len(e.Variants)))
		if i >= len(e.Variants) {
			i = len(e.Variants) - 1
		}
		return e.Variants[i].ID
	}
	p := bucket(e.seed(userID)) * total
	acc := 0.0
	for _, v := range e.Variants {
		if v.Weight <= 0 {
			continue
		}
		acc += v.Weight
		if p < acc {
			return v.ID
		}
	}
	return e.Variants[len(e.Variants)-1].ID
}

// seed — что именно хешируется. Версия внутри: подняли версию — перетасовали
// осознанно, и отчёт увидит это как отдельный эксперимент.
func (e experiment) seed(userID string) string {
	return e.Name + "#" + itoa(e.Version) + ":" + userID
}

// layerOf — слой эксперимента. Своё имя вместо пустого, чтобы эксперименты без
// слоя не оказались все в одном общем.
func (e experiment) layerOf() string {
	if e.Layer != "" {
		return e.Layer
	}
	return "\x00" + e.Name
}

// matches — попадает ли игрок в аудиторию эксперимента.
func (s *ExperimentsService) matches(a experimentAudience, userID string) bool {
	if a.Channel != "" {
		if s.auth == nil || s.auth.AttributionOf(userID).Channel() != a.Channel {
			return false
		}
	}
	if a.Payer != "" {
		paid := false
		if s.payments != nil {
			for _, p := range s.payments.Purchases() {
				if p.User == userID {
					paid = true
					break
				}
			}
		}
		want := strings.EqualFold(a.Payer, "yes") || a.Payer == "да"
		if paid != want {
			return false
		}
	}
	if a.Cohort != "" {
		if s.analytics == nil || s.analytics.firstDayOf(userID) != a.Cohort {
			return false
		}
	}
	return true
}

// assign считает группы игрока по всем экспериментам, с учётом слоёв.
//
// Внутри слоя игрок участвует ровно в ОДНОМ эксперименте: слой делится между
// его экспериментами тем же хешем, и попавший в чужую долю просто не участвует
// в остальных. Без этого два теста в одном слое накладываются друг на друга, и
// эффект первого неотличим от эффекта второго.
func (s *ExperimentsService) assign(userID string) map[string]string {
	all := s.cfg.Get()
	byLayer := map[string][]experiment{}
	for _, e := range all {
		if e.Name == "" || len(e.Variants) == 0 {
			continue
		}
		if !s.matches(e.Audience, userID) {
			continue
		}
		byLayer[e.layerOf()] = append(byLayer[e.layerOf()], e)
	}
	out := map[string]string{}
	for layer, exps := range byLayer {
		// Порядок обязан быть устойчивым: карта в Go отдаёт ключи вразнобой, и
		// без сортировки один и тот же игрок получал бы разные эксперименты
		// от запроса к запросу.
		sort.Slice(exps, func(i, j int) bool { return exps[i].Name < exps[j].Name })
		if len(exps) == 1 {
			if v := exps[0].pick(userID); v != "" {
				out[exps[0].Name] = v
			}
			continue
		}
		i := int(bucket("layer:"+layer+":"+userID) * float64(len(exps)))
		if i >= len(exps) {
			i = len(exps) - 1
		}
		if v := exps[i].pick(userID); v != "" {
			out[exps[i].Name] = v
		}
	}
	return out
}

// GET /v1/experiments — группы ЭТОГО игрока. Клиент забирает их на старте и
// держит у себя; abtest() в сценарии читает уже готовый ответ.
func (s *ExperimentsService) handleMine(w http.ResponseWriter, r *http.Request) {
	userID := ""
	if s.auth != nil {
		userID = s.auth.UserFromRequest(r)
	}
	if userID == "" {
		// Без входа делить некого: отдаём пусто, клиент падает на своё
		// локальное деление поровну. Выдумать временную группу нельзя — она
		// сменится, когда игрок войдёт, и разобьёт статистику пополам.
		writeJSON(w, http.StatusOK, map[string]any{"assignments": map[string]string{}})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"assignments": s.assign(userID)})
}

// GET|PUT /v1/admin/experiments — читать и править конфиг из панели.
func (s *ExperimentsService) handleAdmin(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	switch r.Method {
	case http.MethodGet:
		writeJSON(w, http.StatusOK, map[string]any{"experiments": s.cfg.Get()})
	case http.MethodPut:
		var body []experiment
		if json.NewDecoder(http.MaxBytesReader(w, r.Body, 256<<10)).Decode(&body) != nil {
			http.Error(w, "нужен массив экспериментов", http.StatusBadRequest)
			return
		}
		if err := validateExperiments(body, s.cfg.Get()); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		data, _ := json.MarshalIndent(body, "", "  ")
		if err := atomicWrite(s.cfg.path, data, 0o644); err != nil {
			http.Error(w, "не удалось сохранить", http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"ok": true, "experiments": body})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// validateExperiments ловит то, что портит данные молча.
func validateExperiments(next, prev []experiment) error {
	seen := map[string]bool{}
	old := map[string]experiment{}
	for _, e := range prev {
		old[e.Name] = e
	}
	for _, e := range next {
		if strings.TrimSpace(e.Name) == "" {
			return errf("у эксперимента нет имени")
		}
		if seen[e.Name] {
			return errf("эксперимент %q объявлен дважды", e.Name)
		}
		seen[e.Name] = true
		if len(e.Variants) < 2 {
			return errf("%s: вариантов должно быть хотя бы два", e.Name)
		}
		ids := map[string]bool{}
		for _, v := range e.Variants {
			if strings.TrimSpace(v.ID) == "" {
				return errf("%s: у варианта нет имени", e.Name)
			}
			if ids[v.ID] {
				return errf("%s: вариант %q объявлен дважды", e.Name, v.ID)
			}
			ids[v.ID] = true
			if v.Weight < 0 {
				return errf("%s/%s: доля не может быть отрицательной", e.Name, v.ID)
			}
		}
		// ГЛАВНАЯ ПРОВЕРКА. Смена весов без поднятия версии переставляет часть
		// игроков между группами, и данные до и после становятся
		// несравнимыми — при этом отчёт выглядит нормально. Единственное
		// место, где это можно поймать, — здесь.
		if o, ok := old[e.Name]; ok && o.Version == e.Version && weightsDiffer(o, e) {
			return errf("%s: доли изменились, но версия осталась %d. "+
				"Поднимите version — иначе часть игроков молча переедет в другие группы, "+
				"а старые и новые данные смешаются", e.Name, e.Version)
		}
	}
	return nil
}

func weightsDiffer(a, b experiment) bool {
	if len(a.Variants) != len(b.Variants) {
		return true
	}
	for i := range a.Variants {
		if a.Variants[i].ID != b.Variants[i].ID || a.Variants[i].Weight != b.Variants[i].Weight {
			return true
		}
	}
	return false
}
