import { useCallback, useState } from "react";
import MenuBar from "./MenuBar.jsx";
import { monaco } from "../lib/monacoSetup.js";

// Верхняя панель приложения: марка, СТРОКА МЕНЮ (Файл · Правка · Вид · Переход ·
// Помощь), заголовок окна «новелла — открытый файл» и admin-токен справа.
//
// До этого здесь висела цепочка пилюль (Студия/Админка, Library › Название,
// Characters/Script, Коннект, Playground). Каждая пилюля — своё изобретение,
// которое надо разгадывать; владелец так и сказал: «эту срань ни один человек не
// поймёт, сделай обычно — файл, вид и тд». Меню-бар люди знают по любому
// редактору, и главное — в нём ВИДНО, что вообще умеет студия: раньше половина
// возможностей (переводы, тема, экспорт, поиск по всем главам, переход к файлу)
// не имела ни одной точки входа глазами.

const MAC = typeof navigator !== "undefined"
  && /Mac|iPhone|iPad|iPod/.test(navigator.platform || navigator.userAgent || "");
const MOD = MAC ? "⌘" : "Ctrl+";

/* ── мост до открытого IDE ────────────────────────────────────────────────
   Пункт меню обязан ДЕЛАТЬ дело, а действия скрипта живут внутри
   ScriptSection. Правильный шов — проп `cmds`: хост (App.jsx) передаёт готовые
   обработчики, и меню зовёт их. Пока хост их не передал, меню нажимает те же
   кнопки IDE, что человек мышкой, — один и тот же путь, без второй копии
   логики, которая разъедется с первой. Если кнопки на экране нет, пункт меню
   становится НЕАКТИВНЫМ с объяснением в подсказке: молчаливых пустышек в меню
   быть не должно — именно они и создают ощущение «ничего не работает». */

const q = (sel) => document.querySelector(sel);

// Кнопки тулбара скрипта различаем по СЛОВУ в подписи, а не по глифу: глиф
// (❖ ✦ ⌗ ◐ ⤓ 🌐 ↻) — украшение, его меняют не задумываясь, слово живёт дольше.
const toolBtn = (word) => {
  const root = q(".ide-top-actions");
  if (!root) return null;
  return Array.from(root.querySelectorAll("button")).find((b) => b.textContent.includes(word)) || null;
};
const saveBtn = () => q(".ide-top-actions .btn-primary");
const press = (el) => { if (el) el.click(); };

// Живой инстанс редактора. monacoSetup экспортирует ТОТ ЖЕ модуль monaco,
// которым поднят редактор, поэтому Правка работает через публичный API
// (getAction/run), а не через подделку нажатий клавиш.
function editorOf() {
  let eds = [];
  try { eds = (monaco.editor.getEditors && monaco.editor.getEditors()) || []; } catch { return null; }
  return eds.find((e) => e.hasTextFocus())
    || eds.find((e) => { const m = e.getModel(); return m && m.getLanguageId && m.getLanguageId() === "lvns"; })
    || eds[0] || null;
}
function runEditorAction(id) {
  const ed = editorOf();
  if (!ed) return;
  ed.focus();
  const a = ed.getAction ? ed.getAction(id) : null;
  if (a) a.run(); else ed.trigger("menubar", id, null);
}
// Отмена/повтор идут в модель напрямую — ровно то же делает и сам monaco в
// своей команде undo. Через commandService нельзя: та команда ищет
// СФОКУСИРОВАННЫЙ редактор, а в момент клика по меню фокус на кнопке меню.
function undoRedo(kind) {
  const ed = editorOf();
  const m = ed && ed.getModel();
  if (!m) return;
  ed.focus();
  if (kind === "undo") m.undo(); else m.redo();
}
const editorReadOnly = (ed) => {
  try { return !!ed.getRawOptions().readOnly; } catch { return false; }
};

// «Перейти к файлу» и «Найти во всех главах» ScriptSection слушает НА WINDOW
// (Ctrl/Cmd+P и Ctrl/Cmd+Shift+F) — это его единственный публичный вход, и
// синтетическое нажатие попадает туда так же, как настоящее.
function sendHotkey(letter, shift) {
  const up = letter.toUpperCase();
  const init = {
    key: shift ? up : letter,
    code: "Key" + up,
    keyCode: up.charCodeAt(0),
    which: up.charCodeAt(0),
    shiftKey: !!shift,
    bubbles: true,
    cancelable: true,
  };
  if (MAC) init.metaKey = true; else init.ctrlKey = true;
  window.dispatchEvent(new KeyboardEvent("keydown", init));
}

// Новая глава = «+ New ▾» → первый шаблон («Blank chapter»). На новелле без
// глав того меню нет — там в центре экрана своя кнопка «Add the first chapter».
function newChapter() {
  const opener = q(".ide-new > button");
  if (!opener) { press(q(".ide-blank .btn-primary")); return; }
  if (!q(".ide-new-menu")) opener.click();
  // Меню рисует React — оно появится после текущего события, поэтому шаблон
  // жмём кадром позже.
  requestAnimationFrame(() => {
    const item = q(".ide-new-menu button");
    if (item) item.click();
    else opener.click(); // не нашли — не оставляем открытым лишнее меню
  });
}
function openNewMenu() {
  const opener = q(".ide-new > button");
  if (opener && !q(".ide-new-menu")) opener.click();
}

// Список файлов новеллы для меню «Переход». Читаем панель Files, потому что
// только она знает и главы манифеста, и общие .lvns-файлы. Заодно это лечит
// жалобу «отдели названия файлов от глав»: в панели видно ИМЯ ГЛАВЫ («01»),
// а в этом меню — настоящее имя файла, а имя главы уходит вторым, тусклым.
const fileNameIn = (s) => { const m = /([^\s/\\]+\.lvns?)/.exec(s || ""); return m ? m[1] : ""; };
function fileList() {
  const box = q(".ide-files");
  if (!box) return [];
  const out = [];
  let idx = 0;
  for (const el of box.children) {
    if (el.classList.contains("ide-files-group")) {
      out.push({ group: (el.textContent || "").trim() });
      continue;
    }
    const open = el.querySelector(".ide-file-open");
    if (!open) continue; // «No files.» и прочая нефайловая разметка
    const raw = open.getAttribute("title") || "";
    const shown = (el.querySelector(".ide-file-label")?.textContent || "").trim();
    // Человеческое имя главы («01») живёт в ОТДЕЛЬНОЙ строке .ide-file-sub:
    // в .ide-file-label теперь настоящее имя файла, и брать имя главы оттуда
    // значит не взять его вовсе — в меню оставались одни имена файлов.
    const sub = (el.querySelector(".ide-file-sub")?.textContent || "").trim();
    // Имя файла вытаскиваем РЕГУЛЯРКОЙ, а не split("/"): в подсказке строки
    // рядом с путём может стоять что-то ещё («scripts/x.lvns\nГлава 1»), и
    // тогда обрезка по слэшу вернула бы имя файла вместе с этим хвостом.
    const file = fileNameIn(raw) || fileNameIn(shown) || shown;
    out.push({
      idx: idx++,
      raw,
      path: raw.split("\n")[0].trim(),
      file,
      name: sub && sub !== file ? sub : "",
      draft: !!el.querySelector(".ide-file-tag"),
      active: el.classList.contains("active"),
    });
  }
  if (out.length && !out[0].group) out.unshift({ group: "главы" });
  return out;
}
function openFile(idx, raw) {
  const rows = Array.from(document.querySelectorAll(".ide-files .ide-file-open"));
  press(rows.find((b) => b.getAttribute("title") === raw) || rows[idx]);
}

// Снимок состояния IDE на момент ОТКРЫТИЯ меню: что есть на экране, что уже
// включено, какие файлы существуют. Снимок, а не подписка: меню живёт секунду,
// а следить за чужим DOM постоянно — это лишняя работа на каждый кадр.
function snapshot() {
  const tog = (word) => { const b = toolBtn(word); return b ? { on: b.classList.contains("on") } : null; };
  const sb = saveBtn();
  const problems = q(".ide-status-toggle");
  const ed = editorOf();
  return {
    ide: !!q(".ide"),
    save: sb ? { disabled: !!sb.disabled, label: (sb.textContent || "").trim() } : null,
    reload: !!toolBtn("Reload"),
    copy: !!toolBtn("Copy"),
    preview: tog("Compiled"),
    docs: tog("Reference"),
    examples: tog("Examples"),
    theme: tog("Theme"),
    exp: tog("Export"),
    translate: tog("Languages"),
    problems: problems ? { on: problems.classList.contains("on") } : null,
    canCreate: !!q(".ide-new > button") || !!q(".ide-blank .btn-primary"),
    editor: !!ed,
    readOnly: ed ? editorReadOnly(ed) : false,
    files: fileList(),
  };
}

export default function TopBar({ nav, status, creds, cmds = {} }) {
  const [open, setOpen] = useState(null);
  const [p, setP] = useState(() => ({ files: [] }));
  const [busy, setBusy] = useState(false);

  const admin = nav.mode === "admin";
  const inside = !admin && nav.titleId != null;
  const scripting = inside && nav.section === "script";

  // Меню строится из снимка, поэтому снимок обновляем ровно при открытии.
  const onOpen = useCallback((id) => {
    if (id) setP(snapshot());
    setOpen(id);
  }, []);

  // Действие сначала спрашиваем у хоста (App.jsx может передать настоящий
  // обработчик через cmds) и только потом идём в мост.
  const cmd = (name, bridge) => () => {
    const f = cmds[name];
    if (typeof f === "function") f(); else bridge();
  };

  const goLibrary = () => { nav.setMode("studio"); nav.goHome(); };

  // «Коннект» — одна кнопка между «у меня есть ИИ» и «мой ИИ работает в этой
  // студии». Скачивает файл, который можно целиком вставить в чат с любой
  // моделью: внутри адрес ЭТОГО сервера, ключ записи и весь язык (1600 строк),
  // так что дальше человеку достаточно сказать словами, какую игру он хочет.
  //
  // Скачивание идёт через fetch, а не через <a href>: ссылка не умеет слать
  // заголовок Authorization, а класть ключ в query-параметр значит оставить его
  // в истории браузера, в логах прокси и в реферерах.
  async function downloadAgentBundle() {
    if (busy) return;
    setBusy(true);
    try {
      const r = await fetch("/v1/admin/agent-bundle", {
        headers: { Authorization: "Bearer " + creds.token },
      });
      if (!r.ok) throw new Error(r.status === 401 ? "неверный admin token" : "сервер ответил " + r.status);
      const blob = await r.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = "elvin-agent.md";
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (e) {
      alert("Не получилось скачать: " + e.message);
    } finally {
      setBusy(false);
    }
  }

  // Одна честная фраза на всё меню: почему пункт про скрипт сейчас недоступен.
  const why = admin
    ? "Доступно в режиме «Студия»"
    : !inside
      ? "Сначала откройте новеллу: Файл → Библиотека новелл"
      : !scripting
        ? "Откройте «Вид → Скрипт»"
        : "";
  const ideOff = !p.ide;
  const edOff = !p.editor;
  const edWhy = p.editor ? "" : (why || "Редактор ещё не открыт");

  const menus = [
    {
      id: "file",
      label: "Файл",
      items: [
        {
          id: "new", label: "Новая глава", disabled: !p.canCreate, title: p.canCreate ? "Пустая глава — станет настоящей после сохранения" : why,
          onSelect: cmd("newChapter", newChapter),
        },
        {
          id: "new-tpl", label: "Новая глава из шаблона…", disabled: !p.canCreate,
          title: p.canCreate ? "Откроет список шаблонов в панели файлов слева" : why,
          onSelect: cmd("newFromTemplate", openNewMenu),
        },
        { sep: true },
        {
          id: "save",
          // Три разных дела под одной кнопкой, и меню обязано называть то самое:
          // черновик главы ПУБЛИКУЕТСЯ, живая глава сохраняется в приложение, а
          // общий файл (его подключают через include) в приложение не идёт вовсе
          // — пишется только он сам. Обещать ему «в приложение» — врать.
          label: !p.save ? "Сохранить"
            : /publish/i.test(p.save.label) ? "Опубликовать в приложении"
              : /file/i.test(p.save.label) ? "Сохранить файл"
                : "Сохранить в приложение",
          hint: MOD + "S",
          disabled: !p.save || p.save.disabled,
          title: p.save && p.save.disabled ? "Сначала исправьте ошибки — сохранение заблокировано" : why,
          onSelect: cmd("save", () => press(saveBtn())),
        },
        {
          id: "reload", label: "Перечитать с сервера", disabled: !p.reload,
          title: p.reload ? "Перечитать .lvns с сервера; несохранённые правки будут отброшены" : why,
          onSelect: cmd("reload", () => press(toolBtn("Reload"))),
        },
        { sep: true },
        {
          id: "export", label: "Экспорт проекта…", checked: !!(p.exp && p.exp.on), disabled: !p.exp, title: p.exp ? "" : why,
          onSelect: cmd("export", () => press(toolBtn("Export"))),
        },
        {
          id: "translate", label: "Переводы и языки…", checked: !!(p.translate && p.translate.on), disabled: !p.translate, title: p.translate ? "" : why,
          onSelect: cmd("translate", () => press(toolBtn("Languages"))),
        },
        {
          id: "connect", label: "Скачать файл для ИИ", disabled: !creds.token || busy,
          title: creds.token
            ? "Адрес студии, ключ записи и весь язык одним файлом — вставьте его в чат с ИИ"
            : "Сначала введите admin token справа — файл содержит ключ записи",
          onSelect: cmd("connect", downloadAgentBundle),
        },
        { sep: true },
        { id: "library", label: "Библиотека новелл", onSelect: cmd("openLibrary", goLibrary) },
      ],
    },
    {
      id: "edit",
      label: "Правка",
      items: [
        {
          id: "undo", label: "Отменить", hint: MOD + "Z", disabled: edOff || p.readOnly,
          title: p.readOnly ? "Глава открыта только для чтения (импорт из articy)" : edWhy,
          onSelect: cmd("undo", () => undoRedo("undo")),
        },
        {
          id: "redo", label: "Повторить", hint: (MAC ? "⇧⌘" : "Ctrl+Shift+") + "Z", disabled: edOff || p.readOnly,
          title: p.readOnly ? "Глава открыта только для чтения (импорт из articy)" : edWhy,
          onSelect: cmd("redo", () => undoRedo("redo")),
        },
        { sep: true },
        {
          id: "find", label: "Найти в файле…", hint: MOD + "F", disabled: edOff, title: edWhy,
          onSelect: cmd("find", () => runEditorAction("actions.find")),
        },
        {
          id: "replace", label: "Заменить…", hint: MAC ? "⌥⌘F" : "Ctrl+H", disabled: edOff || p.readOnly,
          title: p.readOnly ? "Глава открыта только для чтения (импорт из articy)" : edWhy,
          onSelect: cmd("replace", () => runEditorAction("editor.action.startFindReplaceAction")),
        },
        {
          id: "find-all", label: "Найти во всех главах…", hint: (MAC ? "⇧⌘" : "Ctrl+Shift+") + "F",
          disabled: ideOff, title: why,
          onSelect: cmd("findAll", () => sendHotkey("f", true)),
        },
        { sep: true },
        {
          id: "goto-line", label: "Перейти к строке…", hint: MAC ? "⌃G" : "Ctrl+G", disabled: edOff, title: edWhy,
          onSelect: cmd("gotoLine", () => runEditorAction("editor.action.gotoLine")),
        },
        {
          id: "copy-lvn", label: "Копировать .lvn в буфер", disabled: !p.copy,
          title: p.copy ? "Скомпилированный скрипт — то, что читает движок" : why,
          onSelect: cmd("copyLvn", () => press(toolBtn("Copy"))),
        },
      ],
    },
    {
      id: "view",
      label: "Вид",
      items: [
        {
          id: "v-characters", label: "Персонажи", checked: inside && nav.section === "characters",
          disabled: !inside, title: inside ? "" : why,
          onSelect: () => nav.setSection("characters"),
        },
        {
          id: "v-script", label: "Скрипт", checked: scripting, disabled: !inside, title: inside ? "" : why,
          onSelect: () => nav.setSection("script"),
        },
        { sep: true },
        {
          id: "v-preview", label: "Скомпилированный вид", checked: !!(p.preview && p.preview.on), disabled: !p.preview, title: p.preview ? "" : why,
          onSelect: cmd("togglePreview", () => press(toolBtn("Compiled"))),
        },
        {
          id: "v-problems", label: "Проблемы", checked: !!(p.problems && p.problems.on), disabled: !p.problems, title: p.problems ? "" : why,
          onSelect: cmd("toggleProblems", () => press(q(".ide-status-toggle"))),
        },
        {
          id: "v-theme", label: "Тема оформления…", checked: !!(p.theme && p.theme.on), disabled: !p.theme, title: p.theme ? "" : why,
          onSelect: cmd("theme", () => press(toolBtn("Theme"))),
        },
        { sep: true },
        {
          id: "v-play", label: "Playground", hint: "↗",
          title: "Песочница: пиши .lvns — играет сразу в браузере",
          onSelect: () => window.open("/play/", "_blank", "noopener"),
        },
        { sep: true },
        { id: "v-studio", label: "Студия", checked: !admin, onSelect: () => nav.setMode("studio") },
        { id: "v-admin", label: "Админка", checked: admin, onSelect: () => nav.setMode("admin") },
      ],
    },
    {
      id: "go",
      label: "Переход",
      items: [
        {
          id: "g-file", label: "Перейти к файлу…", hint: MOD + "P", disabled: ideOff, title: why,
          onSelect: cmd("quickOpen", () => sendHotkey("p", false)),
        },
        {
          id: "g-symbol", label: "Перейти к сцене или метке…", hint: (MAC ? "⇧⌘" : "Ctrl+Shift+") + "O",
          disabled: edOff, title: edWhy,
          onSelect: cmd("gotoSymbol", () => runEditorAction("editor.action.quickOutline")),
        },
        { sep: true },
        ...(p.files && p.files.length
          ? p.files.map((f, i) => (f.group
            ? { group: f.group }
            : {
              id: "g-" + f.path + i,
              label: f.file,
              sub: f.name,
              hint: f.draft ? "черновик" : "",
              checked: f.active,
              title: f.path,
              onSelect: () => openFile(f.idx, f.raw),
            }))
          : [{ id: "g-none", label: "Файлов не видно", disabled: true, title: why || "Список файлов ещё не загрузился" }]),
      ],
    },
    {
      id: "help",
      label: "Помощь",
      items: [
        {
          id: "h-docs", label: "Справочник языка", checked: !!(p.docs && p.docs.on), disabled: !p.docs, title: p.docs ? "Все команды .lvns с примерами" : why,
          onSelect: cmd("docs", () => press(toolBtn("Reference"))),
        },
        {
          id: "h-examples", label: "Примеры", checked: !!(p.examples && p.examples.on), disabled: !p.examples, title: p.examples ? "Готовые куски скрипта — вставляются в открытый файл" : why,
          onSelect: cmd("examples", () => press(toolBtn("Examples"))),
        },
      ],
    },
  ];

  // Заголовок окна, а не цепочка пилюль: слева — что открыто (новелла), справа
  // — какой файл правим. Имя файла берём из creds.path: ScriptSection пишет
  // туда путь при каждом открытии, так что это единственный честный источник.
  //
  // .lvn → .lvns СПЕЦИАЛЬНО: в creds.path у главы лежит путь СКОМПИЛИРОВАННОГО
  // файла, а правится всегда исходник рядом с ним. Без этого заголовок окна
  // звал открытый файл «ec-ch01.lvn», а список файлов и плашка редактора — тем
  // же «ec-ch01.lvns»: два имени одного файла в одном окне — ровно та путаница,
  // из-за которой владелец и просил «сделай обычно — файл, вид и тд».
  const file = scripting ? String(creds.path || "").split("/").pop().replace(/\.lvn$/, ".lvns") : "";
  const place = admin ? "Админка" : inside ? (nav.titleName || nav.titleId) : "Библиотека новелл";

  return (
    <header className="topbar app-bar">
      <div className="brand">
        <button className="brand-mark as-link" onClick={goLibrary} title="Библиотека новелл">
          ELVIN <em>Studio</em>
        </button>
        <MenuBar menus={menus} open={open} onOpen={onOpen} />
      </div>

      <div className="win-title">
        <span className="win-place">{place}</span>
        {file && (
          <>
            <span className="win-dash">—</span>
            <span className="win-file">{file}</span>
          </>
        )}
      </div>

      <div className="topbar-actions">
        {scripting && status && (
          <span className={"badge " + status.kind} title={status.title || ""}>{status.text}</span>
        )}
        <input
          className="field token"
          type="password"
          placeholder="admin token"
          title="Admin token (server -admin-token)"
          value={creds.token}
          onChange={(e) => creds.setToken(e.target.value)}
        />
      </div>
    </header>
  );
}
