import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// In development the browser talks only to Vite (http://localhost:5173); /api is proxied to the ASP.NET Core API.
// That keeps cookies first-party and makes the Google redirect URI  http://localhost:5173/api/auth/google/callback.
const api = process.env.VITE_API_TARGET ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, strictPort: true, proxy: { "/api": { target: api, changeOrigin: false } } },
  preview: { port: 5173, strictPort: true, proxy: { "/api": { target: api, changeOrigin: false } } },
  test: { environment: "node", include: ["src/**/*.test.ts"] },
});
