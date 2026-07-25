import js from "@eslint/js";
import globals from "globals";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";

// Flat config, deliberately small: core recommended + the two React plugins
// that catch real bugs (hooks rules, JSX-aware unused-vars). Style stays the
// author's business.
export default [
  // Ignores are listed one by one instead of a blanket `public/**`: ESLint will
  // not traverse into an ignored directory, so a `!public/play/*.js` negation
  // there silently does nothing (verified — a planted call to an undefined
  // function still passed). Blanket-ignoring `public/**` left the whole web
  // player with NO static check, and it is the surface the README sends
  // newcomers to first.
  {
    ignores: [
      "dist/**",
      "node_modules/**",
      "public/docs/**", // docs viewer: vendored highlighters + generated content
      "public/wasm_exec.js", // Go's wasm glue, vendored verbatim
      "public/play/wasm_exec.js",
    ],
  },
  js.configs.recommended,
  {
    files: ["public/play/*.js"],
    ignores: ["public/play/wasm_exec.js"], // vendored Go runtime glue, not ours
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: "module",
      globals: { ...globals.browser },
    },
    rules: {
      "no-unused-vars": ["error", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      "no-empty": ["error", { allowEmptyCatch: true }],
    },
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
