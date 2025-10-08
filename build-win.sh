#!/usr/bin/env bash
set -e  # stop on any error

# === CONFIG ===
PROJECT_NAME="Aetheris"
RUNTIME="win-x64"
FRAMEWORK="net9.0"  # change this if you're using a different target
ZIP_NAME="${PROJECT_NAME}_Windows.zip"

# Save where the script was run from
RUN_DIR="$(pwd)"

echo "🔧 Building $PROJECT_NAME for Windows..."

# 1. Publish the project
dotnet publish -c Release -r "$RUNTIME" \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:DebugType=None \
  /p:DebugSymbols=false

# 2. Figure out where the output ended up
OUTPUT_DIR="bin/Release/${FRAMEWORK}/${RUNTIME}/publish"

# 3. Go there and zip it up
echo "📦 Zipping build..."
cd "$OUTPUT_DIR"
zip -r "$RUN_DIR/$ZIP_NAME" ./* > /dev/null

# 4. Back to where we started
cd "$RUN_DIR"

echo "🎉 Done!"
echo "Created: $RUN_DIR/$ZIP_NAME"
