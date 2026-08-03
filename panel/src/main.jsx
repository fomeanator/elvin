import React from "react";
import { createRoot } from "react-dom/client";
import "@fontsource-variable/fraunces";
import "@fontsource-variable/hanken-grotesk";
import "@fontsource/ibm-plex-mono/400.css";
import "@fontsource/ibm-plex-mono/500.css";
import "./styles/global.css";
import "./styles/views.css";
import App from "./App.jsx";
import LoginGate from "./components/LoginGate.jsx";

createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <LoginGate>
      <App />
    </LoginGate>
  </React.StrictMode>
);
