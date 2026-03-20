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

# Unity outputs Build/[BuildName]/ with index.html inside. Flatten so index.html is at root.
SUBDIRS=("$BUILD_DIR"/*/)
if [ ${#SUBDIRS[@]} -eq 1 ] && [ -d "${SUBDIRS[0]}" ]; then
  cp -r "${SUBDIRS[0]}"* "$DEPLOY_DIR/"
else
  cp -r "$BUILD_DIR"/* "$DEPLOY_DIR/"
fi

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
