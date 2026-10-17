import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import App from "./App";
import { AuthProvider } from "./auth";
import { ConfigProvider } from "./config";
import { PlaceProvider } from "./place";
import { applyTheme, getTheme } from "./theme";
import "./styles.css";

applyTheme(getTheme());   // before the first paint, so there is no flash of the wrong theme

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <ConfigProvider>
        <AuthProvider>
          <PlaceProvider>
          <App />
          </PlaceProvider>
        </AuthProvider>
      </ConfigProvider>
    </BrowserRouter>
  </StrictMode>,
);
