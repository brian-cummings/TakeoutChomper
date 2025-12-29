# TakeoutChomper – End-to-End Plan

## Goals
- Download all Google Takeout zip parts via authenticated browser automation
- Process each zip exactly once
- Extract videos only
- Deduplicate videos globally
- Persist progress so the process:
  - Is idempotent
  - Survives crashes or reboots
- Automatically delete zip files after successful processing

Non-goals:
- Preserve Google Photos albums
- Parse JSON metadata
- Maintain original Takeout folder structure

---

## Architecture Overview

The system has **three cooperating components**:

1. **Downloader (.NET Playwright console app)**
   - Responsible only for authenticated downloads
   - No processing logic
   - Writes zips to a known folder

2. **Processor (C# console app)**
   - Watches for completed zip files
   - Processes one zip at a time
   - Extracts, deduplicates, stores videos
   - Deletes zip on success

3. **State Store (SQLite)**
   - Tracks lifecycle of every zip
   - Enables idempotency and recovery

Each component can be stopped and restarted independently.

---

## Projects

This repository contains two .NET console applications:

- `TakeoutChomper.Downloader`
  - Purpose: authenticated Google Takeout downloads
  - Entry point: `Program.cs`
  - Dependencies: Microsoft.Playwright, Microsoft.Data.Sqlite

- `TakeoutChomper.Processor`
  - Purpose: zip extraction, video deduplication, cleanup
  - Entry point: `Program.cs`
  - Dependencies: Microsoft.Data.Sqlite

---

## Folder Layout

TakeoutChomper/
├─ TakeoutChomper.Downloader/
│  ├─ TakeoutChomper.Downloader.csproj
│  └─ Program.cs
├─ TakeoutChomper.Processor/
│  ├─ TakeoutChomper.Processor.csproj
│  └─ Program.cs
├─ data/
│  ├─ downloads/          # Raw .zip files from Google
│  ├─ temp/               # One-zip-at-a-time extraction
│  ├─ videos/             # Final deduplicated video store
│  └─ state.db            # SQLite state + hashes
└─ README.md

---

## State Model (SQLite)

### Table: `zips`
Tracks where each Takeout zip is in the pipeline.

| Column        | Type      | Notes |
|--------------|-----------|------|
| zip_name     | TEXT PK   | Filename only |
| status       | TEXT      | `pending`, `downloading`, `downloaded`, `processing`, `done`, `failed` |
| size_bytes   | INTEGER   | Optional sanity check |
| started_at   | DATETIME  | Processing start |
| completed_at | DATETIME  | Processing end |
| error        | TEXT      | Last error if failed |

### Zip State Transitions

| From        | To          | Trigger |
|-------------|-------------|--------|
| (none)      | pending     | Discovered on Takeout page |
| pending     | downloading | Click download |
| downloading | downloaded  | File exists on disk |
| downloaded  | processing  | Processor starts |
| processing  | done        | Zip fully processed |
| processing  | failed      | Exception |
| failed      | downloaded  | Manual retry |

---

### Table: `videos`
Global deduplication registry.

| Column        | Type      | Notes |
|--------------|-----------|------|
| sha256       | TEXT PK   | Content hash |
| file_path    | TEXT      | Final storage location |
| first_seen   | DATETIME  | When imported |
| size_bytes   | INTEGER   | Validation |

This guarantees:
- No duplicate videos
- Re-runs do nothing for already-seen content

### Video File Definition

A video file is identified solely by file extension:
- .mp4
- .mov
- .m4v
- .avi
- .3gp
- .mts

No MIME sniffing or metadata parsing is required.

### Deduplication Rules

- SHA-256 is computed on full file contents
- Hashing occurs before moving the file
- Two files with the same hash are considered identical
- Filename, size, and timestamps are irrelevant for deduplication

---

## Component 1: Playwright Downloader

### Responsibilities
- Use real Chrome profile (logged-in Google account)
- Navigate to Google Takeout download page
- Click each “Download” button
- Save files to `data/downloads/`
- Never rename files
- Never delete files

Implemented as a .NET console application using Microsoft.Playwright.

### Behavior Rules
- Skip downloads that already exist on disk
- Do not mark zip as downloaded in DB until file fully exists
- Let Chrome handle retries and auth

### Failure Handling
- If browser crashes: restart script
- Already-downloaded zips are skipped automatically

---

## Component 2: Zip Processor (C#)

### Processing Loop
Runs continuously or on demand:

1. Query DB for next zip with:
   - `status = 'downloaded'`
2. Mark zip as `processing`
3. Clear `data/temp/`
4. Extract zip into `data/temp/`
5. Recursively scan for video files
6. For each video:
   - Compute SHA-256 hash
   - If hash exists in `videos` table:
     - Skip
   - Else:
     - Move to `data/videos/`
     - Record in DB
7. Mark zip as `done`
8. Delete zip file
9. Clear temp directory

### Temp Directory Rules

- `data/temp` is used for exactly one zip at a time
- It is deleted and recreated before each extraction
- Its contents are never trusted across runs

### Explicit Non-Goals (Processor)

Processor must NOT:
- Download files
- Parse Google Photos JSON metadata
- Preserve album structure
- Rename files for chronological ordering

---

## Idempotency Guarantees

| Scenario | Result |
|--------|-------|
| Reboot during download | Browser resumes |
| Reboot mid-extraction | Zip remains, status resets to `downloaded` |
| Re-run processor | Already-done zips skipped |
| Duplicate videos across zips | Stored once |
| Same zip reappears | Hashes prevent duplication |

No step is destructive until completion is recorded.

---

## Failure Strategy

- Any exception:
  - Zip marked `failed`
  - Error stored in DB
- Failed zips can be retried by:
  - Setting status back to `downloaded`
- No silent data loss

---

## Ordering & Concurrency

- **Single zip at a time**
- **Single processor instance**
- Browser can download in parallel, processor stays serial

This is deliberate to avoid:
- Disk thrashing
- Race conditions
- Hard-to-debug partial states

---

## Technology Choices (Opinionated)

- Playwright for .NET: single-language stack, shared tooling, future-proof
- SQLite over JSON: atomic, queryable, crash-safe
- SHA-256 over filename heuristics: correctness > speed
- No Google APIs: quotas and fragility avoided

---

## Milestones

1. Initialize repo + folders
2. Create SQLite schema
3. Playwright downloader working
4. Processor extracts videos
5. Deduplication enabled
6. Zip cleanup automated
7. Long-running soak test

---

## Success Criteria

- You can reboot mid-run and lose nothing
- Re-running the app produces no duplicates
- Final `videos/` folder contains exactly one copy of each video
- All zip files are eventually deleted

---

All implementations should favor correctness and restart safety over performance or parallelism.
