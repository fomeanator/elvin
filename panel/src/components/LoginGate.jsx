import { createContext, useCallback, useEffect, useState } from "react";
import { me, login as doLogin } from "../lib/session.js";

// Кто вошёл. Читают те, кому нужно спрятать чужое: раздел «Доступ»
// показывают владельцу, а смотрящему незачем видеть кнопки, которые
// сервер ему всё равно не даст нажать.
export const Who = createContext(null);

// ВОРОТА ПАНЕЛИ: пока не знаем, кто пришёл, внутрь не пускаем.
//
// Важно, что это именно обёртка вокруг всего приложения, а не окошко поверх
// него. Панель при запуске сама тянет манифест, список новелл и ассеты; если
// нарисовать форму поверх уже смонтированного приложения, то к моменту, когда
// человек её увидит, запросы уже ушли — и посторонний, открывший адрес,
// увидит за формой названия чужих новелл. Отказ должен наступать ДО того, как
// приложение вообще начнёт существовать.
export default function LoginGate({ children }) {
  const [who, setWho] = useState(undefined); // undefined — ещё спрашиваем
  const [down, setDown] = useState("");

  const ask = useCallback(() => {
    me().then(setWho).catch((e) => { setDown(String(e.message || e)); setWho(null); });
  }, []);
  useEffect(ask, [ask]);

  if (who === undefined) return <div className="gate gate-wait">Проверяем доступ…</div>;
  if (who) return <Who.Provider value={who}>{children}</Who.Provider>;
  return <LoginForm onDone={ask} down={down} />;
}

function LoginForm({ onDone, down }) {
  const [login, setLogin] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState("");

  const submit = async (e) => {
    e.preventDefault();
    setBusy(true); setErr("");
    try {
      await doLogin(login, password);
      onDone();
    } catch (ex) {
      setErr(String(ex.message || ex));
      setPassword("");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="gate">
      <form className="gate-card" onSubmit={submit}>
        <div className="gate-title">Elvin Studio</div>
        <div className="gate-sub">Вход для тех, кто ведёт новеллы</div>

        <label className="gate-field">
          <span>Имя</span>
          {/* autoFocus: сюда пришли ровно за одним действием */}
          <input value={login} onChange={(e) => setLogin(e.target.value)} autoFocus autoComplete="username" />
        </label>
        <label className="gate-field">
          <span>Пароль</span>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
        </label>

        {err && <div className="gate-err">{err}</div>}
        {down && !err && <div className="gate-err">{down}</div>}

        <button className="gate-go" disabled={busy || !login || !password}>
          {busy ? "Проверяем…" : "Войти"}
        </button>

        {/* Пароль заводит владелец студии — самообслуживания здесь нет и не
            должно быть: панель управляет чужими новеллами и чужими деньгами. */}
        <div className="gate-foot">Доступ выдаёт владелец студии</div>
      </form>
    </div>
  );
}
