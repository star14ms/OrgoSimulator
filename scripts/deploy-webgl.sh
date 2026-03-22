#!/bin/bash
# Deploy Unity WebGL build to Vercel
# Prerequisites:
#   1. Build in Unity: File > Build Settings > WebGL > Build
#   2. Set compression to Brotli: Edit > Project Settings > Player > Web > Publishing Settings
#   3. Install Vercel CLI: npm i -g vercel

set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
BUILD_DIR="$PROJECT_ROOT/Build"
DEPLOY_DIR="$PROJECT_ROOT/webgl-deploy"

if [ ! -d "$BUILD_DIR" ]; then
  echo "Error: Build folder not found at $BUILD_DIR"
  echo "Build in Unity first: File > Build Settings > WebGL > Build"
  exit 1
fi

mkdir -p "$DEPLOY_DIR"
echo "Copying build output to webgl-deploy..."
rm -rf "$DEPLOY_DIR"/*

# Unity usually outputs Build/<BuildName>/ with index.html inside. Flatten so index.html is at deploy root.
# If several folders exist (e.g. WEBGL_DEPLOY + _old), pick one that contains index.html — prefer WEBGL_DEPLOY, else newest by mtime.
webgl_mtime() {
  if stat -f %m "$1" >/dev/null 2>&1; then
    stat -f %m "$1"
  else
    stat -c %Y "$1"
  fi
}

SOURCE_DIR=""
if [ -f "$BUILD_DIR/index.html" ]; then
  SOURCE_DIR="$BUILD_DIR"
elif [ -f "$BUILD_DIR/WEBGL_DEPLOY/index.html" ]; then
  SOURCE_DIR="$BUILD_DIR/WEBGL_DEPLOY"
else
  newest=""
  newest_t=0
  for d in "$BUILD_DIR"/*/; do
    [ -d "$d" ] || continue
    [ -f "${d}index.html" ] || continue
    t=$(webgl_mtime "$d")
    if [ "$t" -ge "$newest_t" ]; then
      newest_t=$t
      newest="$d"
    fi
  done
  if [ -n "$newest" ]; then
    SOURCE_DIR="$newest"
  fi
fi

if [ -z "$SOURCE_DIR" ] || [ ! -f "$SOURCE_DIR/index.html" ]; then
  echo "Error: No folder under $BUILD_DIR contains index.html (expected Unity WebGL build output)."
  echo "Build in Unity: File > Build Settings > WebGL, choose output under Project/Build/<name>/"
  exit 1
fi

echo "Using WebGL source: $SOURCE_DIR"
cp -r "$SOURCE_DIR"/* "$DEPLOY_DIR/"

cp "$PROJECT_ROOT/webgl-config/vercel.json" "$DEPLOY_DIR/"

if [ ! -f "$DEPLOY_DIR/vercel.json" ]; then
  echo "Error: vercel.json not found"
  exit 1
fi

if [ ! -f "$DEPLOY_DIR/index.html" ]; then
  echo "Error: index.html not found at deploy root. Check Unity build output structure."
  exit 1
fi

echo "Deploying to Vercel..."
cd "$DEPLOY_DIR"
vercel --prod
