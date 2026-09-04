import js from "@eslint/js";
import globals from "globals";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";

// Flat config, deliberately small: core recommended + the two React plugins
// that catch real bugs (hooks rules, JSX-aware unused-vars). Style stays the
// author's business.
export default [
  // Ignores are listed one by one instead of a blanket `public/**`: ESLint will
  // not traverse into an ignored directory, so a negation inside it silently
  // does nothing.
  {
    ignores: [
      "dist/**",
      "node_modules/**",
      "public/docs/**", // docs viewer: vendored highlighters + generated content
      // Прослойка Go к wasm, взятая дословно. Путь ИМЕННО такой: песочница
      // переехала в public/play/, а исключение осталось на старом месте — и
      // линт годами разбирал сгенерированный файл, давая сотню no-undef.
      // Гейт, красный по построению, не охраняет ничего.
      // ОБЕ копии: файл лежит и в public/, и в public/play/, побайтово
      // одинаковый. Исключение стояло только на первой, а песочница читает
      // вторую — линт разбирал сгенерированный Go файл и давал сотню no-undef.
      // Заменить путь мало, надо перечислить оба: одну копию я расисключил
      // починкой и тут же получил тринадцать новых.
      "public/wasm_exec.js",
      "public/play/wasm_exec.js",
    ],
  },
  js.configs.recommended,
  // СОБСТВЕННЫЕ СКРИПТЫ ПЕСОЧНИЦЫ — это браузер, а не модуль сборки: без
  // объявленных глобалей каждый window и document читаются как «не определено».
  // Их надо не исключать, а описать: там живой код, и настоящие опечатки в нём
  // ловить стоит.
  {
    files: ["public/play/*.js"],
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: "module", // это ES-модули: «script» даёт ошибку разбора на import
      globals: { ...globals.browser, Go: "readonly" },
    },
    // Тот же принятый в панели приём, что и в src/: пустой catch с
    // комментарием. Правило не наследуется между блоками, его надо повторить —
    // иначе один и тот же код в двух каталогах судится по-разному.
    rules: { "no-empty": ["error", { allowEmptyCatch: true }] },
  },
  {
    files: ["src/**/*.{js,jsx}", "test/**/*.js", "*.config.js"],
    plugins: { react, "react-hooks": reactHooks },
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: "module",
      parserOptions: { ecmaFeatures: { jsx: true } },
      globals: { ...globals.browser, ...globals.node },
    },
    settings: { react: { version: "detect" } },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "react/jsx-uses-vars": "error",
      "react/jsx-uses-react": "error",
      "no-unused-vars": ["error", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      // The panel talks to a same-origin dev server; empty catch with a
      // comment is an accepted local idiom.
      "no-empty": ["error", { allowEmptyCatch: true }],
    },
  },
];
