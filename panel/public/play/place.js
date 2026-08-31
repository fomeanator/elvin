// ИМЕНОВАННЫЕ МЕСТА — тот же словарь, что у движка (Placement.SlotNames).
//
// Плеер знал три слова из девяти: «left», «right» и «всё остальное — центр».
// Числа у него тоже были свои (0.22/0.78 против 0.25/0.75 у движка), так что
// одна и та же сцена расставляла героев по-разному в приложении и по ссылке.
// А «offscreen_left» — слово, которое подсказывает редактор и принимает
// компилятор, — ставило актёра в ЦЕНТР кадра вместо того, чтобы увести его.
const SLOTS = {
  // За кадром: доля НАМЕРЕННО вне [0,1] — фигура уходит целиком.
  offscreen_left: -0.25,
  far_left: 0.12,
  left: 0.25,
  center_left: 0.38,
  center: 0.50,
  center_right: 0.62,
  right: 0.75,
  far_right: 0.88,
  offscreen_right: 1.25,
};

/** Доля холста для именованного места. Незнакомое слово — центр. */
export function slotX(position) {
  if (position === null || position === undefined) return 0.5;
  const p = String(position).trim().toLowerCase();
  return p in SLOTS ? SLOTS[p] : 0.5;
}

/** Слова словаря — для сверки со стражем. */
export const LVN_SLOT_NAMES = Object.keys(SLOTS);
