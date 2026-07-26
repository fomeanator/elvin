import { useEffect, useRef } from "react";

// Строка меню приложения — та самая «как везде»: Файл · Правка · Вид · Переход ·
// Помощь. Компонент СПЕЦИАЛЬНО ничего не знает про студию: он получает готовое
// дерево меню и отвечает только за поведение настоящего меню-бара, потому что
// именно поведение люди узнают глазами и пальцами:
//   • клик по пункту открывает меню, повторный клик закрывает;
//   • при уже открытом меню наведение на соседний пункт ПЕРЕКЛЮЧАЕТ меню
//     (в macOS/Windows так, и без этого меню-бар ощущается набором кнопок);
//   • клик вне, Escape и выбор пункта закрывают.
// Что пункты делают — целиком дело TopBar.jsx.
//
// menus: [{ id, label, items }]
// items: [{ id?, label, hint?, sub?, checked?, disabled?, title?, onSelect }
//         | { sep: true } | { group: "заголовок" }]
export default function MenuBar({ menus, open, onOpen }) {
  const barRef = useRef(null);

  useEffect(() => {
    if (!open) return;
    // pointerdown, а не click: меню должно закрыться в тот момент, когда человек
    // нажал мимо, а не после того, как отпустит кнопку над чем-то другим.
    const onDown = (e) => { if (barRef.current && !barRef.current.contains(e.target)) onOpen(null); };
    // Escape НЕ гасим stopPropagation'ом: тот же Escape нужен редактору и
    // диалогам поиска, а перехватить его «на всякий случай» — значит сломать их.
    const onKey = (e) => { if (e.key === "Escape") onOpen(null); };
    document.addEventListener("pointerdown", onDown);
    window.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onDown);
      window.removeEventListener("keydown", onKey);
    };
  }, [open, onOpen]);

  return (
    <nav className="menubar" role="menubar" ref={barRef}>
      {menus.map((m) => (
        <div className="mb-item" key={m.id}>
          <button
            type="button"
            className={"mb-btn" + (open === m.id ? " open" : "")}
            aria-haspopup="menu"
            aria-expanded={open === m.id}
            onClick={() => onOpen(open === m.id ? null : m.id)}
            onMouseEnter={() => { if (open && open !== m.id) onOpen(m.id); }}
          >
            {m.label}
          </button>
          {open === m.id && (
            <div className="mb-pop" role="menu" aria-label={m.label}>
              {m.items.map((it, i) => {
                if (it.sep) return <div className="mb-sep" key={"sep" + i} />;
                if (it.group) return <div className="mb-group section-label" key={"grp" + i}>{it.group}</div>;
                return (
                  <button
                    type="button"
                    key={it.id || String(it.label) + i}
                    role="menuitem"
                    className={"mb-row" + (it.checked ? " checked" : "")}
                    disabled={!!it.disabled}
                    title={it.title || ""}
                    aria-checked={it.checked ? "true" : undefined}
                    // Сначала закрываем меню, потом действуем: иначе действие,
                    // которое забирает фокус (поиск в редакторе, диалог перехода),
                    // открывалось бы под всё ещё висящим меню.
                    onClick={() => { onOpen(null); if (it.onSelect) it.onSelect(); }}
                  >
                    <span className="mb-check" aria-hidden="true">{it.checked ? "✓" : ""}</span>
                    <span className="mb-label">
                      {it.label}
                      {it.sub ? <em className="mb-sub">{it.sub}</em> : null}
                    </span>
                    {it.hint ? <span className="mb-hint">{it.hint}</span> : null}
                  </button>
                );
              })}
              {m.items.length === 0 && <div className="mb-empty">Нет доступных действий</div>}
            </div>
          )}
        </div>
      ))}
    </nav>
  );
}
