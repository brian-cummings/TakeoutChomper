# TakeoutChomper – Running the Pipeline

This repo contains two .NET console apps that work together:
- **Downloader** (`TakeoutChomper.Downloader`) – drives Chrome via Playwright to click Google Takeout “Download” buttons and saves the zip files.
- **Processor** (`TakeoutChomper.Processor/TakeoutChomper.Processor`) – extracts each zip, keeps only videos, deduplicates by SHA-256, records state in SQLite, and deletes the zip after success.

## Prerequisites
- .NET 8 SDK or newer.
- Google Chrome installed.
- You must be logged into your Google account in the Chrome profile you point Playwright at.

## Data Location
- By default, data lives in `~/Downloads/TakeoutChomperDownloads` with subfolders: `downloads/`, `temp/`, `videos/`, and `state.db`.
- Override with env var `TAKEOUTCHOMPER_DATA` to change the data root.
- Both apps auto-create these folders on start.

## Chrome Profile
- Default: your standard Chrome profile for the current OS.
- Override with env var `TAKEOUTCHOMPER_PROFILE` to point to a specific Chrome user-data directory. Examples:
  - macOS/Linux:
    ```bash
    export TAKEOUTCHOMPER_PROFILE="$HOME/Library/Application Support/Google/Chrome"  # or your profile path
    ```
  - Windows (PowerShell):
    ```powershell
    $env:TAKEOUTCHOMPER_PROFILE = "$env:LOCALAPPDATA\Google\Chrome\User Data"
    ```

## Running the Downloader
1) In a terminal:
   ```bash
   # Optional: override Chrome profile and data/target URLs
   # export TAKEOUTCHOMPER_PROFILE="/path/to/Chrome/User Data"
   # export TAKEOUTCHOMPER_DATA="/path/to/data/root"
   # export TAKEOUTCHOMPER_URL="https://takeout.google.com/manage/archive/<id>"
   dotnet run --project TakeoutChomper.Downloader/TakeoutChomper.Downloader.csproj
   ```
2) The downloader navigates to the Takeout page, discovers all download links, and records them in `state.db` before clicking.
3) It skips any part already on disk or marked downloaded/done, to avoid burning Google’s retry limits. Downloads save to `data/downloads/` with deterministic names (`takeout-<job>-part-###.zip`).
4) Partial `.crdownload` files are cleaned on startup; progress logs show size/speed as downloads complete.

## Running the Processor
1) In another terminal:
   ```bash
   dotnet run --project TakeoutChomper.Processor/TakeoutChomper.Processor.csproj
   ```
2) The processor loops:
   - Picks the next zip with status `downloaded`
   - Clears `temp/`, extracts the zip there
   - Finds video files (`.mp4 .mov .m4v .avi .3gp .mts`)
   - Hashes each video, moves unique ones to `videos/` (named by hash), records in `videos` table
   - Marks zip `done` and deletes the zip
3) Safe to restart; interrupted zips reset to `downloaded` on next run. Press Ctrl+C to stop after the current step.

## Tips
- Run only one processor instance at a time; the downloader can run concurrently.
- If you relocate data, set `TAKEOUTCHOMPER_DATA` before running both apps.
- The SQLite file (`state.db`) is the source of truth for zip status and dedup hashes. Do not delete it unless you want to start over.

## One-command runner
Use the helper script to build both apps, start the processor in the background, and run the downloader in the foreground:
```bash
bash run.sh
```
Optional env vars before running:
- `TAKEOUTCHOMPER_DATA` – override data root (default `~/Downloads/TakeoutChomperDownloads`)
- `TAKEOUTCHOMPER_PROFILE` – Chrome user-data directory to use for auth
- `TAKEOUTCHOMPER_URL` – override the page the downloader opens (defaults to the Takeout downloads page)
- `TAKEOUTCHOMPER_URL` – override the page the downloader opens (default `https://takeout.google.com/settings/takeout/downloads`)

Login tip: the downloader now suppresses the Chrome automation banner to reduce Google’s “this browser may not be secure” block. Use a profile that is already signed in to Google to avoid the login prompt entirely.
