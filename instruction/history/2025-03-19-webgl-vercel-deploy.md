# 2025-03-19 — WebGL Vercel deploy

## Summary (since last /compact)

Added WebGL deployment to Vercel with Brotli compression support. Created `webgl-config/vercel.json` with headers for `.br`, `.wasm.br`, `.js.br`, MIME types, and COOP/COEP for multithreading. Added `scripts/deploy-webgl.sh` that flattens Unity build output (`Build/[Name]/` → deploy root) so `index.html` is served at the site root, fixing 404 on `/`. Documented workflow in `WEBGL_DEPLOY.md`. Updated `.gitignore` for `webgl-deploy/` staging. Also present: URP settings, Packages manifest, and Build Profiles changes. Three local commits ahead of origin (edit mode, bonds fixes).
