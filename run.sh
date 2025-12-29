#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Using repo at: $ROOT"
echo "Data root: ${TAKEOUTCHOMPER_DATA:-$HOME/Downloads/TakeoutChomperDownloads}"

echo "Building apps..."
dotnet build "$ROOT/TakeoutChomper.Downloader/TakeoutChomper.Downloader.csproj"
dotnet build "$ROOT/TakeoutChomper.Processor/TakeoutChomper.Processor.csproj"

echo "Starting processor in background..."
dotnet run --project "$ROOT/TakeoutChomper.Processor/TakeoutChomper.Processor.csproj" &
PROCESSOR_PID=$!

cleanup() {
  echo "Stopping processor (pid $PROCESSOR_PID)..."
  kill "$PROCESSOR_PID" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "Launching downloader in foreground..."
dotnet run --project "$ROOT/TakeoutChomper.Downloader/TakeoutChomper.Downloader.csproj"

echo "Downloader exited. Waiting for processor to finish current work..."
wait "$PROCESSOR_PID" 2>/dev/null || true
