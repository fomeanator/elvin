// ВХОД В ПАНЕЛЬ ПО ИМЕНИ — вместо строки-токена, которую все передавали друг
// другу в переписке.
//
// Секрет сессии живёт в cookie, которую ставит сервер и не отдаёт скрипту
// (HttpOnly). Это не формальность: панель грузит и показывает содержимое
// новелл, и любой упавший в неё чужой скрипт — от расширения браузера до
// подсунутой в текст ссылки — первым делом читает localStorage. Токен там
// лежал открыто; секрет сессии прочитать нельзя, а браузер всё равно
// приложит его к запросу сам.
//
// Отсюда правило для всех запросов панели: credentials: "include". Без него
// fetch не приложит cookie, если сборка отдаётся с CDN-адреса.

export const withCreds = (opt = {}) => Object.assign({ credentials: "include" }, opt);

// me — кто мы. null означает «никто»: панель показывает форму входа.
// Отдельный случай — вход по токену машины: сервер отвечает login: "",
// и панель ведёт себя как раньше, не спрашивая пароль у скрипта сборки.
export async function me() {
  const r = await fetch("/v1/admin/session/me", withCreds());
  if (r.status === 401) return null;
  if (!r.ok) throw new Error("сервер недоступен");
  return r.json();
}

export async function login(login_, password) {
  const r = await fetch("/v1/admin/session/login", withCreds({
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ login: login_, password }),
  }));
  if (!r.ok) throw new Error(((await r.text()) || "не вышло войти").trim());
  return r.json();
}

export async function logout() {
  await fetch("/v1/admin/session/logout", withCreds({ method: "POST" })).catch(() => {});
}

// Управление людьми — доступно только владельцу; панель прячет раздел, но
// решает всё равно сервер.
export async function people() {
  const r = await fetch("/v1/admin/people", withCreds());
  if (!r.ok) throw new Error(((await r.text()) || "не показать").trim());
  return (await r.json()).people || [];
}

export async function setPerson(login_, password, role) {
  const r = await fetch("/v1/admin/people", withCreds({
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ login: login_, password, role }),
  }));
  if (!r.ok) throw new Error(((await r.text()) || "не сохранить").trim());
}

export async function removePerson(login_) {
  const r = await fetch("/v1/admin/people?login=" + encodeURIComponent(login_), withCreds({ method: "DELETE" }));
  if (!r.ok) throw new Error(((await r.text()) || "не удалить").trim());
}

export const ROLES = [
  { id: "owner", name: "владелец", hint: "всё, включая доступ других людей" },
  { id: "editor", name: "редактор", hint: "сцены, тексты, ассеты, публикация" },
  { id: "viewer", name: "смотрящий", hint: "только смотреть, ничего не менять" },
];
