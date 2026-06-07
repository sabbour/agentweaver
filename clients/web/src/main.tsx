import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import App from "./App";

const root = document.getElementById("root");
if (!root) {
  throw new Error("Root element not found");
}

createRoot(root).render(
  <StrictMode>
    <FluentProvider theme={webLightTheme}>
      <App />
    </FluentProvider>
  </StrictMode>
);
