import { useEffect, useRef } from "react";

// A drag-to-resize grip pinned to one edge of its parent panel. It writes the
// parent's width directly (no React state, so any panel becomes resizable just
// by dropping this in) and remembers it per `storageKey`. The parent must be
// position: relative and a fixed-width flex item (flex: none).
export default function ResizeHandle({ storageKey, side = "right", min = 220, max = 820 }) {
  const ref = useRef(null);

  useEffect(() => {
    const el = ref.current?.parentElement;
    if (!el) return;
    const saved = parseInt(localStorage.getItem(storageKey), 10);
    if (saved && saved >= min && saved <= max) el.style.width = saved + "px";
  }, [storageKey, min, max]);

  const onPointerDown = (e) => {
    const el = ref.current?.parentElement;
    if (!el) return;
    e.preventDefault();
    const x0 = e.clientX;
    const w0 = el.getBoundingClientRect().width;
    const move = (ev) => {
      const delta = side === "right" ? ev.clientX - x0 : x0 - ev.clientX;
      const w = Math.max(min, Math.min(max, w0 + delta));
      el.style.width = w + "px";
    };
    const up = () => {
      localStorage.setItem(storageKey, String(parseInt(el.style.width, 10)));
      document.removeEventListener("pointermove", move);
      document.removeEventListener("pointerup", up);
      document.body.classList.remove("col-resizing");
    };
    document.body.classList.add("col-resizing");
    document.addEventListener("pointermove", move);
    document.addEventListener("pointerup", up);
  };

  const reset = (e) => {
    // double-click clears the saved width → back to the CSS default
    const el = ref.current?.parentElement;
    if (!el) return;
    e.preventDefault();
    el.style.width = "";
    localStorage.removeItem(storageKey);
  };

  return (
    <div
      ref={ref}
      className={"resize-handle " + side}
      onPointerDown={onPointerDown}
      onDoubleClick={reset}
      title="Drag to resize · double-click to reset"
    />
  );
}
