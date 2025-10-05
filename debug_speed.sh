#!/usr/bin/env bash
set -e

TRACE_DIR="traces"
mkdir -p "$TRACE_DIR"

TIMESTAMP=$(date +%Y%m%d_%H%M%S)
TRACE_FILE="$TRACE_DIR/dotnet_${TIMESTAMP}.nettrace"
SPEEDSCOPE_FILE="$TRACE_DIR/dotnet_${TIMESTAMP}.speedscope.json"
SERVER_LOG="$TRACE_DIR/server_${TIMESTAMP}.log"
CLIENT_LOG="$TRACE_DIR/client_${TIMESTAMP}.log"

# === CLEANUP ===
cleanup() {
    echo "[CLEANUP] Killing trace and server..."
    if [[ -n "$TRACE_PID" && -d "/proc/$TRACE_PID" ]]; then
        kill -INT "$TRACE_PID" 2>/dev/null || true
        wait "$TRACE_PID" || true
    fi
    if [[ -n "$SERVER_PID" && -d "/proc/$SERVER_PID" ]]; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" || true
    fi
}
trap cleanup EXIT

# === Step 1: Start server normally ===
echo "[STEP 1] Starting server..."
dotnet run -- --server >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
echo "[INFO] Server PID: $SERVER_PID"

# Wait a moment for server to bind port
sleep 5

# === Step 2: Start dotnet-trace attached to server ===
echo "[STEP 2] Attaching dotnet-trace to server..."
dotnet-trace collect \
    --process-id "$SERVER_PID" \
    --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x1F000080018:5 \
    --output "$TRACE_FILE" &
TRACE_PID=$!
echo "[INFO] Trace PID: $TRACE_PID"

# === Step 3: Launch client ===
echo "[STEP 3] Launching client..."
dotnet run -- --client >"$CLIENT_LOG" 2>&1 || true

# === Step 4: Client exited, stop trace and server ===
echo "[STEP 4] Client exited, stopping trace and server..."
cleanup

# Wait a moment for trace to finalize
sleep 2

# === Step 5: Convert to SpeedScope ===
echo "[STEP 5] Converting trace to SpeedScope format..."
dotnet trace convert --format speedscope "$TRACE_FILE"

echo "[DONE] Trace saved to: $TRACE_FILE"
echo "[DONE] SpeedScope JSON saved to: $SPEEDSCOPE_FILE"
echo "[INFO] Open it at https://www.speedscope.app/"
