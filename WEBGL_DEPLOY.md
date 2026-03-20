# Deploying OrgoSimulator (Unity WebGL) to Vercel

## Summary

**You do not need `package.json` or `index.js` for Vercel.** Those files are for self-hosted Node.js/Express servers. Vercel serves static files directly from the edge.

For Vercel, you only need:
- **`vercel.json`** – headers for Brotli compression, MIME types, and WebAssembly streaming
- **Unity WebGL build output** – built with Brotli compression

## Unity Build Settings

1. **Edit > Project Settings > Player > Web > Publishing Settings**
   - **Compression Format**: Brotli
   - **Decompression Fallback**: Disabled (so you get `.br` files for native browser decompression)

2. **File > Build Settings > WebGL > Build**
   - Choose output folder (default: `Build/`)

## Deployment

### Option A: Deploy script (recommended)

```bash
# 1. Build in Unity first
# 2. Install Vercel CLI: npm i -g vercel
# 3. Run:
chmod +x scripts/deploy-webgl.sh
./scripts/deploy-webgl.sh
```

### Option B: Manual deploy

```bash
# 1. Build in Unity
# 2. Copy config and deploy:
cp webgl-config/vercel.json Build/
cd Build && vercel --prod
```

## Files Created

| File | Purpose |
|------|---------|
| `webgl-config/vercel.json` | Brotli headers, MIME types, COOP/COEP for multithreading |
| `scripts/deploy-webgl.sh` | Copies build to webgl-deploy (flattens `Build/[Name]/` so `index.html` is at root) and runs Vercel |
| `webgl-deploy/` | Staging folder (gitignored contents) |

**Note:** Unity outputs to `Build/[BuildName]/`. The deploy script flattens this so `index.html` is served at the site root.

## Headers in vercel.json

- **`.br`** → `Content-Encoding: br`
- **`.wasm.br`** → `Content-Type: application/wasm` + `Content-Encoding: br`
- **`.js.br`** → `Content-Type: application/javascript` + `Content-Encoding: br`
- **`.wasm`** → `Content-Type: application/wasm`
- **`.js`** → `Content-Type: application/javascript`
- **COOP/COEP** on HTML for WebAssembly multithreading (SharedArrayBuffer)
