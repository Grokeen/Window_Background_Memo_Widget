# Notedex

**Windows desktop notes with Markdown files as the source of truth.** Notedex combines a searchable notes manager with separate desktop memo windows. This repository contains the **0.3.0 development preview source**, not a finished or signed installer.

> **Release status:** the candidate passes its functional, UI, and startup probes, but it does not meet the current memory target. The repository does not include a release executable. Windows shell behavior and physical-device acceptance remain incomplete.

## What it does

- Saves each note body as a UTF-8 `.md` file and autosaves edits.
- Offers visual Markdown editing for headings, emphasis, lists, and task checklists, with access to the Markdown source.
- Organizes notes with folders, tags, pinning, and priority levels.
- Opens a separate widget for the same note, with per-note background color, font size, and placement settings.
- Supports validated archive backup and restore.
- Includes an optional Windows sign-in startup setting, off by default. This repository does not register the app to run at login.

The manager supports wide and compact layouts. These are Windows desktop screens informed by a notes-style three-pane reference; they are not a pixel replica of the iPhone Notes app.

### Current candidate screenshots

All screenshots use synthetic test notes and show the WPF app content, not a physical Windows desktop or shell integration test.

| Notes manager | Compact editor | Memo widget |
|---|---|---|
| ![Wide notes manager](docs/screenshots/candidate-01/notes-manager-wide.png) | ![Compact editor](docs/screenshots/candidate-01/notes-manager-compact.png) | ![Memo widget](docs/screenshots/candidate-01/notes-widget.png) |

## User manual

- [Korean user manual (PDF)](output/pdf/Notedex_Notes_User_Manual_KO.pdf)
- [Korean user manual source](Markdown/USER_MANUAL_NOTES.md)

## Build and test

Requirements: Windows and the .NET SDK version pinned in `global.json` (10.0.402).

```powershell
dotnet build WindowBackgroundMemoWidget.slnx -c Release
dotnet run --project tests/WindowBackgroundMemoWidget.Tests/WindowBackgroundMemoWidget.Tests.csproj -c Release
```

The development team also runs Windows-only isolated WPF probes from `scripts/`. They use synthetic profiles; do not point them at personal memo data.

## Verification snapshot

The current source was rebuilt after refreshing the embedded guide pages: **0 warnings and 0 errors**. The regression run passed 141 Markdown checks and 12/12 integration groups. The refreshed guide passed six isolated startup/restart launches. The UI probe (156 recorded entries including diagnostics), compact editor measurement (399 DIP), and search p95 (127 ms) belong to the previously verified UI candidate.

The latest direct memory measurements predate this guide refresh: 154.0 MiB for a returning user and 190.7 MiB on first run, both over the 150 MiB target. Memory was not remeasured on the refreshed candidate, so its current result is unknown and the release gate remains unmet.

Not yet accepted on physical Windows: Korean IME, varied DPI and multi-monitor placement, Explorer/Win+D and sleep recovery, login startup, and eight-hour stability. The root-level executable is stale and is not part of this repository snapshot. No GitHub Release or installer is provided.

## Repository layout

- `src/` — WPF application.
- `tests/` — isolated functional and integration checks.
- `scripts/` — Windows build and test helpers.
- `docs/screenshots/` — screenshots from the verified candidate.
