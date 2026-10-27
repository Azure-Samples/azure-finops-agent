import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      "/auth": "http://localhost:5000",
      "/api": "http://localhost:5000",
    },
  },
  build: {
    outDir: "../backend/wwwroot",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks: (id) => {
          if (id.includes("echarts") || id.includes("zrender")) return "echarts";
        },
      },
    },
  },
});
