import js from "@eslint/js";
import globals from "globals";
import react from "eslint-plugin-react";
import reactHooks from "eslint-plugin-react-hooks";

// Flat config, deliberately small: core recommended + the two React plugins
// that catch real bugs (hooks rules, JSX-aware unused-vars). Style stays the
// author's business.
export default [
  { ignores: ["dist/**", "public/**", "node_modules/**"] },
  js.configs.recommended,
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
