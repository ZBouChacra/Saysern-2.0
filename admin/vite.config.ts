import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// The back office is its own web app (own build, own host). In development it runs on :5174 and proxies /api to the ASP.NET Core API,
// so its cookie stays first-party. In production serve it on its own host (for example admin.<your domain>) and route /api to the API there.
const api = process.env.VITE_API_TARGET ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react()],
  server: { port: 5174, strictPort: true, proxy: { "/api": { target: api, changeOrigin: false } } },
  preview: { port: 5174, strictPort: true, proxy: { "/api": { target: api, changeOrigin: false } } },
  test: { environment: "node", include: ["src/**/*.test.ts"] },
});
