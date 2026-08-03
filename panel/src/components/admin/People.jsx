import { useCallback, useEffect, useState } from "react";
import { Page, LoadState, Confirm, Drawer } from "./ui.jsx";
import { people, setPerson, removePerson, logout, ROLES } from "../../lib/session.js";

// КТО ИМЕЕТ ДОСТУП К СТУДИИ.
//
// Раздел намеренно скучный: список имён, право и кнопка отобрать. Вся его
// ценность в том, что до него доступ был один на всех — общая строка-токен,
// которую нельзя ни отобрать у одного, ни разделить по правам, ни потом
// вспомнить, кому её вообще отправляли.
export default function People({ me, notify }) {
  const [rows, setRows] = useState(null);
  const [error, setError] = useState("");
  const [editing, setEditing] = useState(null); // {login, role} либо {} для нового
  const [killing, setKilling] = useState("");

  const load = useCallback(() => {
    setError("");
    people().then(setRows).catch((e) => { setRows([]); setError(String(e.message || e)); });
  }, []);
  useEffect(load, [load]);

  const remove = async (login) => {
    try {
      await removePerson(login);
      notify && notify(`Доступ отозван: ${login}`);
      load();
    } catch (e) {
      notify && notify(String(e.message || e), "error");
    }
  };

  return (
    <Page
      title="Доступ"
      count={rows ? rows.length : undefined}
      description="Кто может входить в панель и что каждому позволено. Пароль знает только его владелец — здесь его можно лишь заменить."
      actions={<button className="btn btn-primary" onClick={() => setEditing({ login: "", role: "editor" })}>Добавить человека</button>}
    >
      <LoadState loading={rows === null} error={error} empty={rows && !rows.length}
                 emptyText="Пока никого — вход только по токену сборки">
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead>
              <tr><th>имя</th><th>право</th><th>заведён</th><th>последний вход</th><th /></tr>
            </thead>
            <tbody>
              {(rows || []).map((p) => {
                const role = ROLES.find((r) => r.id === p.role);
                const self = me && me.login === p.login;
                return (
                  <tr key={p.login}>
                    <td>{p.login} {self && <span className="muted">— это вы</span>}</td>
                    <td title={role ? role.hint : ""}>{role ? role.name : p.role}</td>
                    <td className="muted">{shortDate(p.created)}</td>
                    <td className="muted">{p.last_seen ? shortDate(p.last_seen) : "—"}</td>
                    <td>
                      <button className="btn-ghost sm" onClick={() => setEditing({ login: p.login, role: p.role })}>Изменить</button>{" "}
                      {/* Себя из списка не убираем: запертая снаружи студия
                          чинится только руками на сервере. */}
                      {!self && <button className="btn-ghost sm" onClick={() => setKilling(p.login)}>Отозвать</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </LoadState>

      {editing && (
        <PersonForm
          init={editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); load(); notify && notify("Сохранено"); }}
          notify={notify}
        />
      )}
      {killing && (
        <Confirm
          title={`Отозвать доступ у «${killing}»?`}
          body="Открытые сессии этого человека закроются сразу же."
          dangerLabel="Отозвать"
          onConfirm={() => remove(killing)}
          onClose={() => setKilling("")}
        />
      )}

      <p className="muted" style={{ marginTop: 18 }}>
        Вы вошли как <b>{me && me.login ? me.login : "токен сборки"}</b>.{" "}
        <button className="btn-ghost sm" onClick={() => logout().then(() => window.location.reload())}>Выйти</button>
      </p>
    </Page>
  );
}

function PersonForm({ init, onClose, onSaved, notify }) {
  const editing = !!init.login;
  const [login, setLogin] = useState(init.login || "");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState(init.role || "editor");
  const [busy, setBusy] = useState(false);

  const save = async (e) => {
    e.preventDefault();
    setBusy(true);
    try {
      await setPerson(login, password, role);
      onSaved();
    } catch (ex) {
      notify && notify(String(ex.message || ex), "error");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer title={editing ? `Изменить «${init.login}»` : "Новый человек"} onClose={onClose} width={420}>
      <form onSubmit={save} className="gate-form">
        <label className="gate-field">
          <span>Имя</span>
          <input className="field" value={login} disabled={editing} autoFocus={!editing}
                 onChange={(e) => setLogin(e.target.value)} />
        </label>

        <label className="gate-field">
          <span>{editing ? "Новый пароль" : "Пароль"}</span>
          <input className="field" type="password" value={password} autoFocus={editing}
                 autoComplete="new-password" onChange={(e) => setPassword(e.target.value)} />
          {/* Восемь знаков — нижняя граница сервера; повторяем её здесь, чтобы
              отказ не прилетал уже после нажатия. */}
          <small className="muted">не короче восьми знаков</small>
        </label>

        <div className="gate-field">
          <span>Право</span>
          {ROLES.map((r) => (
            <label key={r.id} className="gate-radio">
              <input type="radio" name="role" checked={role === r.id} onChange={() => setRole(r.id)} />
              <span><b>{r.name}</b> — <span className="muted">{r.hint}</span></span>
            </label>
          ))}
        </div>

        <div className="gate-actions">
          <button type="button" className="btn-ghost sm" onClick={onClose}>Отмена</button>
          <button className="btn btn-primary" disabled={busy || !login || password.length < 8}>
            {busy ? "Сохраняем…" : "Сохранить"}
          </button>
        </div>
      </form>
    </Drawer>
  );
}

const shortDate = (s) => {
  if (!s) return "—";
  const d = new Date(s);
  return isNaN(d) ? s : d.toLocaleDateString("ru-RU", { day: "numeric", month: "short", year: "numeric" });
};
