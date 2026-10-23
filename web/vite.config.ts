import { defineConfig } from "vite";

// Dev: `npm run dev` proxies /api to the .NET server. Build: output goes into the server's wwwroot,
// which is embedded in the assembly. File names are not hashed so MSBuild's embedded item list stays valid.
export default defineConfig({
  server: { proxy: { "/api": "http://localhost:5180" } },
  build: {
    outDir: "../src/Siglent.Server/wwwroot",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        entryFileNames: "assets/[name].js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: "assets/[name][extname]",
      },
    },
  },
});
