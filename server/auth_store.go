package main

// Аккаунты в базе: чтение остаётся в памяти, запись становится построчной.
//
// Разделение не компромисс, а разные требования. ЧТЕНИЕ идёт на каждый запрос
// (проверка токена) и должно стоить ноль — карта в памяти это и даёт, а
// двести пятьдесят байт на игрока означают двадцать пять мегабайт на сто
// тысяч, что коробка переживёт. ЗАПИСЬ была узким местом: весь users.json
// переписывался ради одного изменённого поля.
//
// Поэтому здесь ровно два действия: загрузить всё при старте и записать ОДНОГО
// игрока при изменении.
//
// Разовый перенос из users.json делается сам и оставляет исходный файл под
// именем .imported. Удалять его нельзя: пока новый путь не проверен на живых
// данных, единственная копия аккаунтов — это он.

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
)

// loadUsersFromDB поднимает таблицу в память. Возвращает индексы, которые
// сервис держит рядом с картой: по устройству и по внешней идентичности.
func loadUsersFromDB(db *sql.DB) (map[string]*authUser, map[string]string, map[string]string, error) {
	users := map[string]*authUser{}
	byDev := map[string]string{}
	byProv := map[string]string{}

	rows, err := db.Query(`SELECT id, device_hash, token_hash, created, name, attr_json FROM users`)
	if err != nil {
		return nil, nil, nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var id, dev, tok, created, name, attr string
		if err := rows.Scan(&id, &dev, &tok, &created, &name, &attr); err != nil {
			return nil, nil, nil, err
		}
		u := &authUser{DeviceHash: dev, TokenHash: tok, Created: created, Name: name}
		if attr != "" {
			var a playerAttribution
			// Битую запись атрибуции пропускаем, а аккаунт сохраняем: канал
			// привлечения не стоит потерянного игрока.
			if json.Unmarshal([]byte(attr), &a) == nil {
				u.Attr = &a
			}
		}
		users[id] = u
		if dev != "" {
			byDev[dev] = id
		}
	}
	if err := rows.Err(); err != nil {
		return nil, nil, nil, err
	}

	prows, err := db.Query(`SELECT user_id, provider, subject FROM user_providers`)
	if err != nil {
		return nil, nil, nil, err
	}
	defer prows.Close()
	for prows.Next() {
		var uid, provider, subject string
		if err := prows.Scan(&uid, &provider, &subject); err != nil {
			return nil, nil, nil, err
		}
		u := users[uid]
		if u == nil {
			continue // осиротевшая связь: внешние ключи её не пустят, но чтение не место для паники
		}
		if u.Providers == nil {
			u.Providers = map[string]string{}
		}
		u.Providers[provider] = subject
		byProv[provider+":"+subject] = uid
	}
	return users, byDev, byProv, prows.Err()
}

// saveUserLocked пишет ОДНОГО игрока. Вызывающий держит s.mu.
//
// Идентичности переписываются целиком в той же транзакции: их единицы, а
// вычислять разницу значит однажды ошибиться в пользу лишней связи, которая
// отдаст чужой аккаунт.
func (s *AuthService) saveUserLocked(id string) error {
	u := s.users[id]
	if u == nil {
		return fmt.Errorf("нет такого игрока: %s", id)
	}
	if s.db == nil {
		return s.persistFileLocked() // база не подключена — прежний путь
	}
	attr := ""
	if u.Attr != nil {
		if b, err := json.Marshal(u.Attr); err == nil {
			attr = string(b)
		}
	}
	tx, err := s.db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err := tx.Exec(`
		INSERT INTO users(id, device_hash, token_hash, created, name, attr_json)
		VALUES(?,?,?,?,?,?)
		ON CONFLICT(id) DO UPDATE SET
			device_hash=excluded.device_hash,
			token_hash =excluded.token_hash,
			created    =excluded.created,
			name       =excluded.name,
			attr_json  =excluded.attr_json`,
		id, u.DeviceHash, u.TokenHash, u.Created, u.Name, attr); err != nil {
		return err
	}
	if _, err := tx.Exec(`DELETE FROM user_providers WHERE user_id = ?`, id); err != nil {
		return err
	}
	for provider, subject := range u.Providers {
		if _, err := tx.Exec(
			`INSERT INTO user_providers(user_id, provider, subject) VALUES(?,?,?)`,
			id, provider, subject); err != nil {
			return err
		}
	}
	return tx.Commit()
}

// persistFileLocked — прежний путь через users.json. Остаётся для сборок без
// базы и для самого переноса.
func (s *AuthService) persistFileLocked() error {
	data, _ := json.MarshalIndent(s.users, "", "  ")
	return atomicWrite(s.path, data, 0o600)
}

// importUsersFile переносит users.json в базу ОДИН раз: только если в базе
// пусто. Исходный файл переименовывается, а не удаляется — пока новый путь не
// проверен на живых данных, это единственная копия аккаунтов.
func (s *AuthService) importUsersFile() error {
	var n int
	if err := s.db.QueryRow(`SELECT count(*) FROM users`).Scan(&n); err != nil {
		return err
	}
	if n > 0 {
		return nil // база уже наполнена: повторный импорт затёр бы свежее старым
	}
	data, err := os.ReadFile(s.path)
	if err != nil {
		return nil // файла нет — чистый старт, переносить нечего
	}
	var old map[string]*authUser
	if err := json.Unmarshal(data, &old); err != nil {
		return fmt.Errorf("разобрать %s: %w", s.path, err)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.users = old
	s.byDev = map[string]string{}
	s.byProv = map[string]string{}
	for id, u := range old {
		if u.DeviceHash != "" {
			s.byDev[u.DeviceHash] = id
		}
		for provider, subject := range u.Providers {
			s.byProv[provider+":"+subject] = id
		}
		if err := s.saveUserLocked(id); err != nil {
			return fmt.Errorf("перенести %s: %w", id, err)
		}
	}
	if len(old) > 0 {
		_ = os.Rename(s.path, s.path+".imported")
	}
	return nil
}
