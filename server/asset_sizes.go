package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// ОБЪЁМ ИГРЫ — ОТДЕЛЬНЫЙ ОТВЕТ, А НЕ ПОЛЕ В ИНДЕКСЕ ВЕРСИЙ.
//
// Индекс версий читают все клиенты, и добавить туда второе значение — сменить
// формат под всеми сразу. Размеры нужны другому разговору: загрузчик считает
// «сколько всего качать» и «сколько осталось», а игрок выбирает ступень
// качества по её цене в мегабайтах.
//
// ВАРИАНТЫ КАЧЕСТВА СОЗДАЮТСЯ ЛЕНИВО: пока их никто не просил, на диске лежит
// только исходник. Поэтому кроме фактических размеров отдаём КОЭФФИЦИЕНТЫ —
// долю варианта от исходника, посчитанную по тем парам, что уже есть. Клиент
// умножает и получает честную оценку вместо выдуманного числа.

type assetSizes struct {
	Files  map[string]int64   `json:"files"`
	Ratios map[string]float64 `json:"ratios"`
}

type sizeCacheEntry struct {
	data assetSizes
	at   time.Time
}

var (
	sizeMu    sync.Mutex
	sizeCache *sizeCacheEntry
)

// sizesTTL: обход дерева не бесплатен, а объём меняется только с публикацией.
const sizesTTL = 60 * time.Second

func (s *server) handleAssetSizes(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(s.computeSizesCached())
}

func (s *server) computeSizesCached() assetSizes {
	sizeMu.Lock()
	defer sizeMu.Unlock()
	if sizeCache != nil && time.Since(sizeCache.at) < sizesTTL {
		return sizeCache.data
	}
	data := s.computeSizes()
	sizeCache = &sizeCacheEntry{data: data, at: time.Now()}
	return data
}

func (s *server) computeSizes() assetSizes {
	files := map[string]int64{}
	// Пары «исходник → вариант» для коэффициентов: суммируем и делим один раз,
	// иначе среднее по отношениям перекосит мелочь вроде значков.
	srcBytes := map[string]int64{}
	varBytes := map[string]int64{}

	filepath.Walk(s.content, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		rel, rerr := filepath.Rel(s.content, path)
		if rerr != nil {
			return nil
		}
		rel = filepath.ToSlash(rel)
		if info.IsDir() {
			if rel != "." && privateRel(rel+"/") {
				return filepath.SkipDir
			}
			return nil
		}
		if toolingRel(rel) || privateRel(rel) {
			return nil
		}
		files[rel] = info.Size()

		// Коэффициент считаем только по картинкам, у которых есть исходник:
		// звук и скрипты вариантов не имеют, и их доля ничего не описывает.
		base := filepath.Base(rel)
		for _, v := range downscaleVariants {
			if v.suffix == miniSuffix {
				continue // «мини» — не ступень показа, игрок её не выбирает
			}
			if !strings.Contains(base, v.suffix+".") {
				continue
			}
			src := strings.Replace(rel, v.suffix+".", ".", 1)
			if st, e := os.Stat(filepath.Join(s.content, filepath.FromSlash(src))); e == nil {
				varBytes[v.suffix] += info.Size()
				srcBytes[v.suffix] += st.Size()
			}
		}
		return nil
	})

	ratios := map[string]float64{}
	for _, v := range downscaleVariants {
		if v.suffix == miniSuffix {
			continue
		}
		if srcBytes[v.suffix] > 0 {
			ratios[v.suffix] = float64(varBytes[v.suffix]) / float64(srcBytes[v.suffix])
		}
	}
	return assetSizes{Files: files, Ratios: ratios}
}
