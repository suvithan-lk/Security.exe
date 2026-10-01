# SECURITY.EXE — Phase 3

A local-only Windows desktop security application: camera preview with live status
and failure diagnostics, face detection, a five-step face enrollment wizard,
DPAPI-encrypted face embeddings, face recognition, and **background monitoring** —
session observation, camera pause on lock, a tray icon, desktop notifications, and
retention — all running entirely on your machine.

**SECURITY.EXE does not replace Windows authentication.**

This release is a standalone application with its own local database. It does not
interact with Windows sign-in, `winlogon`, the lock screen, the Secure Desktop, or any
credential provider. It never stores, reads, or modifies a password, PIN, or any other
Windows credential. Nothing here is a bypass of, or a substitute for, Windows
authentication.

- **Product:** Security
- **Executable:** `Security.exe` (assembly name `Security`)
- **Version:** 0.3.0 — Phase 3
- **Stack:** C# · .NET 10 · WPF · MVVM · OpenCvSharp · ONNX Runtime · SQLite · EF Core 10

---

## Table of contents

1. [Overview](#overview)
2. [What's new in Phase 3](#whats-new-in-phase-3)
3. [What's new in Phase 2](#whats-new-in-phase-2)
4. [Features](#features)
4. [Architecture](#architecture)
5. [Tech stack](#tech-stack)
6. [Requirements](#requirements)
7. [Install](#install)
8. [Run](#run)
9. [Camera setup](#camera-setup)
10. [Enrollment](#enrollment)
11. [Recognition](#recognition)
12. [Database structure](#database-structure)
13. [Security model](#security-model)
14. [Privacy model](#privacy-model)
15. [Configuration](#configuration)
16. [Tests](#tests)
17. [Limitations](#limitations)
18. [Known issues](#known-issues)
19. [Roadmap](#roadmap)

---

## Overview

SECURITY.EXE watches a webcam for a face and tells you whether the person in front of
the camera matches the single enrolled profile. It is a **local** tool: no account, no
cloud, no telemetry, no network calls except an optional one-time download of the
public ONNX models described below.

On first launch you will see a dark security dashboard with a sidebar — a **SECURITY**
group containing **Dashboard, Face Profile, Camera, Events, Settings, About** — and
status cards for *Windows session*, *Camera*, *Face profile*, *Recognition*,
*Monitoring* and *Last event*, followed by the security timeline, system health and
recent events.

## What's new in Phase 3

Phase 3 turns SECURITY.EXE into a background personal-security monitor without
rebuilding anything that already worked: all Phase 1/2 functionality is preserved.

**Background monitoring**

- `SecurityMonitorService` (a .NET `BackgroundService`) coordinates the session
  observer, the camera lifecycle, event recording, notifications, health reporting and
  retention — all `CancellationToken`-driven, with a clean stop on exit.
- `IWindowsSessionService` records **SessionLocked / SessionUnlocked / SessionLogon /
  SessionLogoff / SessionConnected / SessionDisconnected** using supported Windows APIs
  only (`SystemEvents.SessionSwitch` + a WTS lock-state probe). Unknown state stays
  *Unknown* — never guessed as *Unlocked*.
- **Camera policy:** locking Windows **always** stops the camera; unlocking resumes it
  only when camera monitoring is enabled. No start/stop churn; a failed start is
  retried after `CameraRetryIntervalSeconds`.

**Tray and notifications**

- A notification-area icon (WinForms `NotifyIcon`) with **Open Security, Pause /
  Resume Monitoring, Open Events, Settings, Exit** and an honest status line
  (`MONITORING ACTIVE / PAUSED / OFF`) mirroring the monitor verbatim.
- **X minimises to tray** (default ON), **Exit** performs a full shutdown, and a
  **single-instance mutex** makes a second copy activate the first instead of opening
  a second DB/camera session.
- Desktop notifications: a tray balloon plus a desktop alert window, both gated by a
  30-second notification cooldown, both **never** shown on the lock screen or Secure
  Desktop. Wording is fixed and neutral: *Unknown person detected.*

**Snapshots and retention**

- Unknown-face snapshots are **OFF by default**; when enabled, one frame per cooldown
  window is written to `data/events/` under a generated filename and shown from the
  new **Event Details** dialog.
- `SnapshotRetentionDays` (1/3/7/14/30, default 7) and `EventRetentionDays`
  (default 30) clean up expired files and rows automatically; cleanup summaries go to
  the application log, never the event table.

**Health and UI**

- `IHealthMonitor` reports **Healthy / Degraded / Unavailable** for Database, Camera,
  Recognition engine, Background service and Notifications — concrete faults only,
  never "system compromised".
- Dashboard: a `SYSTEM STATUS` banner, the six status cards, an icon-based **security
  timeline** and a **system health** panel.
- Events: result/session filters, description search, a **SESSION** column and an
  **Event Details** dialog (all fields + snapshot, or *No snapshot stored.*).
- Settings: a **MONITORING** section (background monitoring, session monitoring,
  camera-when-unlocked, unknown-face detection, desktop notifications — all default
  **ON**), notification cooldown, retention rows, and privacy statements.
- The database gained `SessionState` and `SnapshotPath` columns via additive
  migration `20261001070910_Phase3SessionAndSnapshot` (the pre-migration DB was
  backed up first).

## What's new in Phase 2

Phase 2 does not rebuild the application. It diagnoses and fixes the real failures that
stopped the Phase 1 pipeline from producing a single usable result, then rounds out the
camera, enrollment, and recognition experience.

**Fixed root causes**

| Root cause | Effect in Phase 1 |
| --- | --- |
| `long.MinValue` sentinels and unchecked subtraction in the frame-processor and coordinator cooldowns | the throttle never elapses, so detection and recognition ran **zero** times per session |
| `_latestFrame` capture race (frame replaced while a worker held it) | intermittent failures and torn frames |
| `StopAsync` teardown continuation pinned to `CancellationToken.None` | stop could hang the UI |
| `System.Windows.DataTemplate` text bound as a literal on Dashboard/Events | `System.Windows.DataTemplate` printed where a value belonged |
| `OpenDevice` swallowed every exception | a refused camera reported **success** |
| Enumeration probed backend × index before trying WinRT names | ~6 s to list devices |

**New in this release**

- `ICameraService` matches the documented contract (`GetAvailableCamerasAsync`,
  `StartAsync(int)`, `StopAsync`, `RestartAsync`, `IsRunning`, `FrameReceived`) with
  diagnostics for enumeration, open, first frame, and failure — never image contents.
- A four-state camera page: **OFFLINE / CONNECTING / LIVE / ERROR**, with a status
  badge, a status block (Status · Resolution · FPS · Face detection), an aspect-ratio
  preview, a `CAMERA OFFLINE` empty state, and a `CAMERA UNAVAILABLE` failure panel
  listing concrete causes plus **Retry**.
- `IFaceQualityService` — one quality gate shared by live preview and enrollment, so
  the wizard can no longer accept a frame the preview calls unusable.
- A five-step enrollment wizard: **Welcome → Position → Capture → Processing →
  Complete**, with per-sample cooldown so bursts of near-identical frames cannot
  saturate the model.
- Recognition surfaced as **KNOWN / UNKNOWN / UNCERTAIN** with a confidence percentage.
- An app-level toast system (auto-dismiss, manual close, never on the Secure Desktop).
- Settings reorganised into **CAMERA / RECOGNITION / PRIVACY / APPLICATION /
  SECURITY**, every row being *Title · Control · Description*.
- A reusable design system (`PrimaryButton`, `SecondaryButton`, `DangerButton`,
  `GhostButton`, `Card`, `StatusBadge`, `SectionHeader`, `TextInput`, `ComboBox`,
  `DataGrid`, `ProgressBar`, `ToggleSwitch`) replacing default WPF controls.
- `Security.App.Tests`, exercising the camera state machine against fakes.

## Features

**Camera**
- Enumerates connected capture devices and shows them by their Windows friendly names
  (WinRT names first, so listing is fast).
- Start / stop / **restart** / switch device, with graceful handling of disconnects,
  permission denial, and "no camera present".
- Four explicit page states with a reusable `● LIVE / CONNECTING / OFFLINE / ERROR`
  badge: a refused start now ends in **CAMERA UNAVAILABLE** with a list of concrete
  causes and a **Retry**, instead of silently reporting success.
- Status block showing Status, Resolution, FPS (measured over a one-second window) and
  Face detection; an aspect-ratio-correct live preview with the bounding box overlaid.
- Detection status text: *Face detected*, *No face detected*, *Multiple faces
  detected*.
- The camera is released on stop, on device switch, on error, and on application exit.
- Capture runs on a worker thread; the UI is never blocked and every long-running
  operation takes a `CancellationToken`.

**Detection & recognition**
- YuNet face detection with 5-point landmarks.
- Recognition is suppressed entirely while more than one face is in frame.
- A shared `IFaceQualityService` gate reports `FaceQualityResult { IsAcceptable,
  Score, Reason }` with operator-facing reasons: *Move closer*, *Move further away*,
  *Please face the camera*, *Only one person should be visible*, *Lighting is too
  low*, *Image quality is too low*.
- Throttled pipeline (default 8 detections/second) with a single-slot frame buffer,
  a stability requirement, and a recognition cooldown so the model is not hammered.
- Verdicts shown as **KNOWN / UNKNOWN / UNCERTAIN** with a confidence percentage and
  the active threshold.

**Enrollment**
- A five-step wizard: **Welcome → Position → Capture → Processing → Complete**, with
  step chips, Back/Retry/Cancel at every stage, and a completion summary
  (profile name, created date, samples used, model version).
- Captures **10–20 samples** (default 20), prompted for center / left / right / up /
  down head positions, with a progress bar (`Samples captured: 8 / 20`, `40%`) and a
  per-sample cooldown so a burst of near-identical frames cannot saturate the model.
- Each sample is rejected, with a reason, when the frame has: no face, multiple faces,
  blur, a face that is too small / too far / too close / off-centre, or poor lighting.
- Validation failures (no samples, multiple faces, invalid embeddings, model
  unavailable, database/encryption failure, camera disconnect) end the wizard on a
  **Failed** panel offering **Retry** and **Cancel** with the actual reason.
- Samples are embedded by the real ONNX model and averaged into one representation.
- **No photograph is used as an authentication factor.** Raw frames are processed in
  memory and discarded.

**Events**
- ApplicationStarted / ApplicationStopped, CameraStarted / CameraStopped / CameraError,
  EnrollmentStarted / EnrollmentCompleted / EnrollmentFailed, FaceDetected,
  KnownFaceDetected, UnknownFaceDetected, RecognitionFailed, SettingChanged,
  EventsCleared — plus Phase 3: SessionLocked / SessionUnlocked / SessionLogon /
  SessionLogoff / SessionConnected / SessionDisconnected, MonitoringStarted /
  MonitoringStopped / MonitoringPaused / MonitoringResumed, NotificationSent,
  SnapshotCaptured, SnapshotDeleted.
- Events screen with timestamp, event, result, confidence, **session state** and
  description; filters by type, date range, result and session, a description search,
  clear-all behind a confirmation dialog, and an **Event Details** dialog showing every
  field plus the stored snapshot (or *No snapshot stored.*).
- Event rows never contain biometric data.

**Unknown face**
- An in-app alert banner inside the main window, a tray balloon, and a desktop alert
  window — each gated by the 30-second notification cooldown, wording fixed and
  neutral: *Unknown person detected.*
- A `UnknownFaceDetected` security event is recorded (gated by its own 30-second
  cooldown); an image snapshot is stored only when **Store snapshots** is explicitly
  enabled (default **OFF**), under a generated filename in `data/events/`.
- Deliberately **not** shown on the Windows Secure Desktop or the lock screen — the
  alert window opens only in the normal desktop session, and the camera (and with it
  all capture) stops whenever Windows locks.

**Notifications**
- App-level toasts in the bottom-right of the main window: auto-dismiss after six
  seconds (twelve for errors), manually closable, at most four visible at once.
- Tray balloon tips (click restores the window) when **Desktop notifications** is on.
- Rendered inside `MainWindow` / the notification area only — never on the Windows
  Secure Desktop, lock screen, or Action Center.

**Interface**
- Every default WPF control is replaced by a themed style from `DarkTheme.xaml`.
- Restrained dark security theme: no glow, no gradients, no oversized headings.
- Keyboard navigation, tab order, visible focus states, accessible labels, tooltips,
  and contrast are treated as part of the page, not as an afterthought.

## Architecture

```
security/
├── Security.slnx
├── README.md
├── .gitignore
├── models/                  ONNX models (downloaded, git-ignored) + README.md
├── data/                    security.db and its -wal/-shm files (git-ignored)
├── logs/                    rolling Serilog output (git-ignored)
├── scripts/
│   └── download-models.ps1  explicit model fetch
├── src/
│   ├── Security.Core/          entities, enums, interfaces, pure domain logic
│   ├── Security.Data/          EF Core 10, SQLite, repositories, migrations
│   ├── Security.Infrastructure/ DPAPI encryption, settings, event store, Serilog
│   ├── Security.Face/          camera, detection, embedding, recognition, enrollment,
│   │                           liveness, frame pipeline, model locator
│   └── Security.App/           WPF shell: views, view models, converters, styles
└── tests/
    ├── Security.App.Tests/     61 tests — camera state machine, tray/single-instance,
    │                           monitor lifecycle, XAML/theme guard
    ├── Security.Core.Tests/   120 tests
    ├── Security.Face.Tests/    54 tests
    └── Security.Data.Tests/    33 tests
```

**Layering rule:** business logic lives in `Security.*`. The WPF project holds views,
view models, converters, and the composition root — code-behind files contain nothing
but `InitializeComponent()`.

**Key seams (all interfaces live in `Security.Core.Interfaces`)**

| Interface | Purpose |
| --- | --- |
| `ICameraService` | enumerate / start / stop / capture frames |
| `IFaceDetectionService` | detect faces + landmarks in a `Mat` |
| `IFaceEmbeddingService` | `Task<float[]> GenerateEmbeddingAsync(Mat face)` |
| `IFaceRecognitionService` | returns `FaceRecognitionResult` |
| `IFrameProcessor` | throttled capture → detect → recognize pipeline |
| `IEnrollmentService` | multi-sample enrollment state machine |
| `IFaceQualityService` | `FaceQualityResult` gate shared by preview and enrollment |
| `ILivenessService` | liveness placeholder |
| `IDataProtectionService` | encrypt/decrypt before storage |
| `ISettingsService` | `appsettings.json` defaults + DB overrides |
| `ISecurityEventService` | persist and mirror events |
| `IWindowsSessionService` | observe lock/unlock/logon/logoff/connect/disconnect |
| `IHealthMonitor` | per-component Healthy / Degraded / Unavailable reporting |
| `IRetentionService` | age out expired events and their snapshot files |
| `ISnapshotStore` | write / delete one-frame snapshots under `data/events/` |
| `IToastService` | in-app, non-blocking notifications |

**UI flow:** `App.xaml.cs` builds a Generic Host (Microsoft.Extensions DI + Serilog),
shows `MainWindow`, and kicks off a background model preflight. `MainViewModel` owns
sidebar navigation through `INavigationService`, avoiding a circular dependency between
the shell and the screens. A singleton `CameraCoordinator` owns the camera and the
frame pipeline; every screen is a **read-only observer**, so two views can never fight
over the device.

### Data flow per frame

```
camera thread ──► single-slot buffer (older frames dropped)
                     │
                     ▼  worker task, throttled to DetectionFps
              YuNet detection ──► quality gates ──► primary face
                     │
                     ▼  only when 1 face is stable AND cooldown elapsed
              SFace embedding ──► cosine similarity ──► Known / Unknown / Unable
                     │
                     ├─► overlay (bounding box + status) on the UI thread
                     ├─► security event (no biometric payload)
                     └─► unknown-face alert (30 s cooldown → banner/balloon/desktop)
```

## Tech stack

| Concern | Choice |
| --- | --- |
| Runtime / UI | .NET 10, WPF (`net10.0-windows10.0.19041.0`), hand-written MVVM helpers |
| Vision | OpenCvSharp4 `4.13.0.20260627` + `OpenCvSharp4.runtime.win` |
| Models | Microsoft.ML.OnnxRuntime `1.30.0` |
| Persistence | SQLite + Microsoft.EntityFrameworkCore.Sqlite `10.0.12` |
| Cryptography | System.Security.Cryptography.ProtectedData `10.0.12` (DPAPI) |
| Hosting / DI / logging | Microsoft.Extensions.\* `10.0.12`, Serilog 4.4.0 + File/Console sinks |
| Tests | xunit `2.9.3`, Microsoft.NET.Test.Sdk `17.14.1`, coverlet |

Target frameworks: `Security.Core` and `Security.Data` target plain `net10.0`;
`Security.Infrastructure` targets `net10.0-windows`; `Security.Face`, `Security.App`,
and `Security.Face.Tests` target `net10.0-windows10.0.19041.0` (required for WinRT
camera enumeration).

## Requirements

- Windows 10 1809 (build 17763) or later — realistically Windows 11, because WinRT
  `DeviceInformation` enumeration is used for friendly camera names.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10
  **Desktop Runtime** if you run a published build.
- A webcam.
- ~40 MB of disk for the ONNX models, plus the database and logs.

## Install

```powershell
git clone <your-repo-url> security
cd security
dotnet restore
```

### Get the models

The models are **not** committed (≈37 MB of third-party weights). Either:

```powershell
# Option A — explicit
powershell -ExecutionPolicy Bypass -File .\scripts\download-models.ps1
```

or just run the app: on first launch it attempts the same download automatically, but
only when a file is missing and the machine is online. If both are unavailable, the app
still starts and the dashboard shows **Recognition engine: Not Ready**.

| File | Size | Used for | Source / licence |
| --- | --- | --- | --- |
| `face_detection_yunet_2023mar.onnx` | ~227 KB | face detection + 5-point landmarks | [opencv/opencv_zoo](https://github.com/opencv/opencv_zoo), Apache-2.0 |
| `face_recognition_sface_2021dec.onnx` | ~36.9 MB | 128-D face embedding | [opencv/opencv_zoo](https://github.com/opencv/opencv_zoo), Apache-2.0 |

Both are legitimate, publicly published, documented models with a commercially usable
licence. They are loaded from local disk only — never modified, re-hosted, or uploaded.

## Run

```powershell
# from the repository root
dotnet run --project src/Security.App
```

Other useful commands:

```powershell
dotnet build                                   # build everything
dotnet test                                    # run all 268 tests
dotnet run -c Release --project src/Security.App

# headless diagnosis harness: exercises the real DI wiring against the real
# camera, database and models, then prints a PASS/FAIL summary
dotnet run --project tools/Security.Diag/Security.Diag.csproj

# database (only needed after schema changes)
dotnet ef migrations add <Name> --project src/Security.Data --startup-project src/Security.Data
dotnet ef database update   --project src/Security.Data --startup-project src/Security.Data
```

On first run the app creates `data/security.db` and applies the `InitialCreate`
migration, then the additive Phase 3 migration
`20261001070910_Phase3SessionAndSnapshot` (session state + snapshot path columns).
Application logs go to `logs/security-YYYYMMDD.log` (14 days retained).

> **Where files land:** `data/`, `logs/`, and `models/` are resolved from the repository
> root by walking up from the executable looking for `Security.slnx`. In a published
> build with no solution above it, they fall back to the executable's own directory.

## Camera setup

1. Open **Camera** in the sidebar.
2. Pick a device from the list (Windows friendly names are used when OpenCV and WinRT
   agree on the count, otherwise `Camera 1`, `Camera 2`, …).
3. **Start camera**. The badge moves `OFFLINE → CONNECTING → LIVE`; the preview
   appears with a bounding box drawn around detected faces, a live face count, and a
   status block showing Status, Resolution, FPS, and Face detection.
4. **Stop camera** releases the device; **Restart camera** stops and reopens the same
   device (with a short delay so Media Foundation can hand it back), which is also
   what **Retry** on the failure panel does. Switching devices while running restarts
   capture on the new device.

Page states:

| State | What you see |
| --- | --- |
| `OFFLINE` | `CAMERA OFFLINE` with a **Start camera** button |
| `CONNECTING` | spinner and *Opening the capture device…* |
| `LIVE` | preview with the detection overlay |
| `ERROR` | `CAMERA UNAVAILABLE` with a **Common causes** list, **Retry** and **Refresh** |

If Windows has denied camera access: *Settings → Privacy & security → Camera →
Allow desktop apps to access your camera*. The app reports this as
"Camera access was denied" under **Common causes** rather than failing silently.

The dashboard's camera card shows the current state (ready / running / stopped /
no camera detected).

## Enrollment

The flow is a five-step wizard on the **Face Profile** screen. Step chips across the
top show where you are; **Back** and **Cancel** are available at every stage.

| Step | What happens |
| --- | --- |
| **1 · Welcome** | explains what will be captured and how the template is protected |
| **2 · Position** | the camera starts (if needed) and you centre your face; **Next** is enabled only while exactly one face is in frame |
| **3 · Capture** | samples are collected automatically with a progress bar, a pose instruction, and a ~450 ms cooldown between samples |
| **4 · Processing** | samples are embedded, averaged, L2-normalized, encrypted with DPAPI and persisted |
| **5 · Complete** | shows profile name, created date, samples used, and the model version |

1. Open **Face Profile**. With no profile present you are offered the wizard.
2. **Start enrollment** starts the camera if it is not already running.
3. Follow the on-screen prompts — turn your head center, left, right, up, down — while
   the progress bar advances (`Samples captured: 8 / 20`, `40%`).
4. A sample is rejected with an explanation when:
   - no face is visible,
   - more than one face is visible,
   - the face is off-centre (*Please face the camera*),
   - the face is too close (*Move further away from the camera*) or too small/far
     (*Move closer…*),
   - the image is blurry (below `MinimumBlurScore`),
   - the lighting is outside `MinimumBrightness`…`MaximumBrightness`.
5. At the target count the samples' embeddings are averaged, L2-normalized, encrypted
   with DPAPI, and stored. **EnrollmentCompleted** is recorded.

If validation fails — no usable samples, multiple faces, invalid embeddings, the model
is unavailable, the database or encryption layer fails, or the camera disconnects —
the wizard lands on a **Failed** panel showing the actual reason with **Retry** and
**Cancel** rather than silently restarting or reporting success.

You can re-enroll at any time, which replaces the stored embedding.

## Recognition

With a profile enrolled and **Enable recognition** switched on, the pipeline reports:

| Status | Meaning |
| --- | --- |
| `Known` (**KNOWN**) | cosine similarity ≥ the configured threshold |
| `Unknown` (**UNKNOWN**) | a face was analysed and did not match |
| `UnableToDetermine` (**UNCERTAIN**) | no face, multiple faces, poor quality, model not ready, or a failure — never treated as an identity claim |

The result carries `IsMatch`, `Confidence`, `Similarity`, `ProfileId`, and `Timestamp`.
The camera page's **Recognition verdict** panel shows the status, a **Confidence**
percentage, and a detail line such as
`Confidence 87% · similarity 0.742 · threshold 0.60 · 14:03:22`.

> An UNCERTAIN verdict reports no confidence figure (`N/A`) rather than `0%`, because
> "no score" and "confidently not you" are different statements.

### About the 0.60 threshold

`Recognition.Threshold` defaults to **0.60** and is configurable in **Settings**.

> **0.60 is a starting value, not a universal constant.** Cosine-similarity thresholds
> are not transferable between models, camera setups, lighting conditions, or
> distances. A threshold that is too low produces false accepts; too high produces
> false rejects. **It must be calibrated** against your own camera, lighting, and the
> population you expect, and re-calibrated whenever the model or capture setup changes.
> This release ships a documented default, not a tuned one.

Other recognition-related settings: enable/disable recognition, enable/disable the
liveness check, the recognition cooldown (default 5 seconds), the detection rate, and
the enrollment sample count.

### Liveness

`ILivenessService` returns a `LivenessResult { IsLive, Confidence, Method, Timestamp }`.

> **Basic liveness foundation — NOT production anti-spoofing.** In Phase 1 this is a
> placeholder built from image-quality signals. It does **not** defeat photographs,
> video replays, deepfakes, masks, or injected frames, and it must not be relied upon
> as an anti-spoofing control. The liveness toggle defaults to **off**.

## Database structure

`data/security.db` (SQLite, created by EF Core migrations `20260930061313_InitialCreate`
and `20261001070910_Phase3SessionAndSnapshot`):

| Table | Purpose | Personal data |
| --- | --- | --- |
| `UserProfiles` | the single active local profile (display name, timestamps) | display name only |
| `FaceEmbeddings` | DPAPI-protected payload, model version, sample count | encrypted biometric, never plaintext |
| `SecurityEvents` | event type, result, confidence, timestamp, description, session state, snapshot path | none — no biometric payload; an optional snapshot *path* (the file itself lives in `data/events/`) |
| `ApplicationSettings` | key/value overrides on top of `appsettings.json` | none |

The app supports exactly **one** active profile. Only the data the feature actually
needs is stored — no photographs (unless opt-in snapshots are enabled), no frames in
the database, no contact details, no identifiers beyond the local profile row.

## Security model

- **Encryption at rest:** face embeddings are encrypted with Windows DPAPI
  (`DataProtectionScope.CurrentUser`) plus additional entropy before being written, so
  the database row is meaningless to another user account or another machine. Decryption
  happens only in memory, only when needed. An AES implementation
  (`AesDataProtectionService`) exists behind the same interface for portability and is
  covered by round-trip tests.
- **What is stored:** an encrypted 128-float vector, the model version, and a sample
  count. **No raw images are stored in the database**, snapshots are **off** by
  default, and any opt-in snapshot file is a single frame under `data/events/` with an
  automatic retention window.
- **Logging:** embeddings, raw frames, and encrypted payloads are never written to the
  log, and exceptions surfaced to the UI never carry them. The log template destructures
  only the fields the caller passes.
- **Network:** local only. The single optional outbound request is the one-time ONNX
  model download from `opencv_zoo`, and it can be disabled
  (`AddSecurityFace(allowModelDownload: false)`) for air-gapped deployments.
- **Credentials:** the application never requests, reads, stores, or transmits a
  password, PIN, or Windows credential, and never touches Windows authentication,
  `winlogon`, or credential providers.
- **Camera access is visible:** the device is opened only by an explicit user action
  and released on stop, switch, error, and exit.
- **Alerting:** unknown faces produce an in-app banner, a tray balloon, and a desktop
  alert window — all gated by a 30-second notification cooldown, all inside the normal
  desktop session only. Never a system-level or Secure Desktop prompt.

**Background monitoring & tray**
- `SecurityMonitorService` (a .NET `BackgroundService`) observes the Windows session,
  applies the camera policy (lock always stops capture; unlock resumes it when enabled),
  records session and monitoring events, fires notifications, refreshes health and runs
  retention — all `CancellationToken`-driven.
- Session events: SessionLocked / SessionUnlocked / SessionLogon / SessionLogoff /
  SessionConnected / SessionDisconnected, recorded as **Info** using supported Windows
  APIs only.
- A notification-area tray icon with **Open Security · Pause/Resume Monitoring · Open
  Events · Settings · Exit** and an honest status line (`MONITORING ACTIVE / PAUSED /
  OFF`). Closing the window minimises to the tray (default ON); **Exit** performs a
  full shutdown; a single-instance mutex makes a second copy activate the first.
- **Snapshots** (opt-in, default OFF): one frame per unknown-face cooldown window is
  written to `data/events/` under a generated filename, linked from the Event Details
  dialog, and deleted by `SnapshotRetentionDays` (1/3/7/14/30, default 7).
- **Retention:** events older than `EventRetentionDays` (default 30) and their expired
  snapshot files are cleaned up automatically; the cleanup summary goes to the
  application log, never the event table.
- **Health:** Database, Camera, Recognition engine, Background service and
  Notifications each report Healthy / Degraded / Unavailable with a concrete detail.

## Privacy model

- Everything runs locally; there is no account, cloud service, or telemetry.
- Biometric data is minimized to one encrypted embedding per enrolled profile.
- **Store snapshots:** OFF by default (never enables itself). When explicitly enabled,
  frames stay on disk only until `SnapshotRetentionDays` expires.
- **Store security events:** ON by default — events contain metadata only, and expire
  after `EventRetentionDays`.
- Snapshots never appear on the Windows lock screen or Secure Desktop; the camera is
  stopped whenever Windows locks.
- The Events screen lets you clear the log at any time (with confirmation).
- Deleting `data/security.db` removes the profile, the embedding, and the history;
  deleting `data/events/` removes snapshots.

## Configuration

`src/Security.App/appsettings.json` supplies defaults; **Settings** persists overrides
into `ApplicationSettings`.

```jsonc
{
  "Recognition": {
    "Threshold": 0.6,                  // similarity cutoff — see "About the 0.60 threshold"
    "MinimumFaceSize": 120,            // px; smaller faces are rejected
    "RecognitionCooldownSeconds": 5,   // min gap between recognitions
    "DetectionFps": 8,                 // pipeline throttle (5–10 recommended)
    "MinimumBlurScore": 45,            // enrollment quality gate
    "MinimumBrightness": 35,           // enrollment quality gate
    "MaximumBrightness": 220,
    "EnrollmentSampleCount": 20,       // 10–20
    "MinimumFaceRatio": 0.12,
    "StableFaceFrames": 3              // consecutive detections before recognizing
  },
  "Camera":   { "Width": 1280, "Height": 720, "Backend": 0 },
  "Settings": {
    "RecognitionEnabled": true,
    "LivenessCheckEnabled": false,
    "StoreSnapshots": false,           // privacy default; frames go to data/events/
    "StoreSecurityEvents": true,
    "StartWithWindows": false,         // HKCU Run key only
    "MinimizeToTray": true,            // X hides to the tray; Exit is on the tray menu
    "BackgroundMonitoring": true,      // Phase 3 §6 — monitor the session + camera
    "SessionMonitoring": true,         // record lock/unlock/logon/logoff events
    "MonitorCameraWhenUnlocked": true, // lock always stops the camera
    "UnknownFaceDetection": true,      // unknown-face events + cooldown
    "DesktopNotifications": true,      // tray balloon + desktop alert window
    "NotificationCooldownSeconds": 30, // gates balloon + toast + alert window
    "UnknownFaceCooldownSeconds": 30,  // gates unknown-face event + snapshot + alert
    "CameraRetryIntervalSeconds": 60,  // retry a failed auto-start
    "SnapshotRetentionDays": 7,        // {1,3,7,14,30}
    "EventRetentionDays": 30,          // 1–3650
    "CameraWidth": 1280,               // capture size, applied on the next start
    "CameraHeight": 720
  },
  "Serilog":  { "MinimumLevel": "Information" }
}
```

These are **starting defaults**, not tuned values.

**Capture size precedence:** `appsettings.json` → `Camera` section supplies the
default, overridden by a `CameraWidth`/`CameraHeight` entry in the `Settings` section
if present, overridden in turn by the value persisted in the database by
**Settings → CAMERA → Capture resolution**. The effective size is applied the next
time a device is opened.

## Tests

```powershell
dotnet test
```

**268 tests, all passing** (0 warnings):

| Project | Tests | Covers |
| --- | --- | --- |
| `Security.Core.Tests` | 120 | threshold/decision logic, profile validation, event factory (including the Phase 3 session/monitoring/snapshot event types), DPAPI + AES encryption round-trips, settings load/save/sanitize/migration (capture-size persistence, clamping, `MinimizeToTray` migration), primary-face selection and the shared person-counting rule, WTS lock-state struct layout regression, path resolution |
| `Security.Face.Tests` | 54 | face alignment math, static quality gates, the live `IFaceQualityService` gate (face count, detector noise, centring, guidance vocabulary), liveness placeholder, frame-pipeline attach/detach lifecycle |
| `Security.App.Tests` | 61 | camera state machine (no camera, enumeration failure, refused start → ERROR, permission denial, stop, restart, badge transitions), **monitor lifecycle** (start/stop/pause/resume, session transitions, camera lock/unlock policy, unknown-face and notification cooldowns), **single-instance guard**, snapshot/event retention, health status, settings persistence, **plus a XAML guard** that parses `DarkTheme.xaml` the way WPF does and checks every view's resource references — so a broken theme fails CI instead of throwing on every layout pass at runtime |
| `Security.Data.Tests` | 33 | UserProfile / FaceEmbedding / SecurityEvent / ApplicationSetting repositories against a real temp SQLite file, including `SessionState`/`SnapshotPath` persistence and `DeleteOlderThanAsync` |

No test requires camera hardware — camera and model behaviour is behind interfaces, so
the suites use fakes and a temporary database. The scenarios that genuinely need a
device (enumeration timing, first-frame latency, sustained memory, real enrollment)
are covered by the `tools/Security.Diag` harness, which drives the application's real
DI wiring against the real camera:

```powershell
dotnet run --project tools/Security.Diag/Security.Diag.csproj
```

## Limitations

- **SECURITY.EXE does not replace Windows authentication.** No integration with
  sign-in, the lock screen, or any credential provider. Nothing in this release
  changes Windows authentication, `winlogon`, or any credential provider, and the
  app never locks or unlocks your machine.
- **Single profile.** One enrolled identity; no multi-user support.
- **Threshold not calibrated.** 0.60 is a documented starting point only.
- **Liveness is a placeholder**, not production anti-spoofing.
- **Session state can be Unknown.** The WTS probe reports what Windows actually
  reports; when it cannot determine the lock state (or session monitoring is off) the
  state stays *Unknown* — it is never guessed as *Unlocked*, so the camera is not
  auto-started on a guess.
- **Win+L lock/unlock transitions and the tray flows are manual tests.** They need a
  real desktop session (a lock screen cannot be simulated by the unit suites), so they
  are verified by hand with the app running.
- **Detector noise is filtered, not eliminated.** A detection below half the
  configured minimum face size is ignored when deciding whether a second person is
  present. That is the right trade-off for enrollment (YuNet's stray ~15px blips were
  blocking half of all frames), but it means a genuinely present person far enough
  away to be detected at that size will not be counted as a second person.
- **Not a security boundary.** Treat this as a demonstration of a local face-recognition
  pipeline, not as an access-control mechanism. Face recognition can be fooled and can
  misclassify; it must not be the sole gate on anything that matters.
- **One camera at a time**, and the pipeline is deliberately throttled (5–10 FPS
  detection) rather than frame-rate driven.
- **The unit suites are hardware-free by design**, so real-device behaviour —
  enumeration timing, first-frame latency, sustained memory, and actual enrollment
  against your own face — is verified with the `tools/Security.Diag` harness and
  manually in the app.
- Windows-only (WPF, DPAPI, WinRT camera enumeration).

## Known issues

- **Smart App Control blocks unsigned development builds.** If Windows has Smart App
  Control set to **On** (Windows Security → App & browser control → Smart App control),
  newly compiled assemblies are rejected with
  `FileLoadException: ... An Application Control policy has blocked this file (0x800711C7)`
  and recorded as Code Integrity event 3033 ("did not meet the Enterprise signing
  level requirements"). This machine-level setting is outside the application's
  control; the workarounds are to turn Smart App Control off (one-way — it cannot be
  returned to *On* or *Evaluation* without a Windows reset) or to code-sign the
  binaries with a certificate trusted by Windows.
- The repository lives under OneDrive. `models/`, `data/`, and `logs/` are git-ignored,
  but OneDrive may still sync them; move the checkout out of OneDrive if that is
  unwanted.
- OpenCV logs a benign `VIDEOIO/FFMPEG: Failed list devices for backend dshow` warning
  while probing backends, and may emit `Camera index out of range` when probing indices
  past the last device. Neither affects capture.
- The detector is recreated when the frame size changes (this OpenCvSharp build exposes
  no `SetInputSize`), which is a one-off cost per resolution change.
- **Changing the capture resolution in Settings takes effect the next time a device is
  opened.** A camera that is already running keeps its current session; press
  **Restart camera** (or stop then start) to pick up the new size.
- The last-used camera is written to the settings store but is deliberately **not**
  restored across restarts — Windows does not guarantee the same device index, so a
  stale selection could open the wrong camera. The first enumerated device is used
  instead.
- The diagnostic harness stops early when nobody is in front of the camera: the
  hardware stages that need a visible face (detection, enrollment, recognition,
  memory soak) report *skipped*, not *failed*. Sit in front of the camera and re-run
  to exercise them.

## Roadmap

**Phase 4 (not started)**
- Multi-profile support and profile management (rename, delete, re-enroll).
- Threshold calibration tooling against a labelled set of your own captures.
- Real anti-spoofing liveness (challenge-response and/or a dedicated anti-spoof model),
  replacing the placeholder.
- Optional Windows toast notifications (Action Center) for unknown faces, still never
  on the Secure Desktop — Phase 3 ships tray balloons and a desktop alert window only.
- Optional hardware signing / MSIX packaging to satisfy Smart App Control.

**Explicitly out of scope for all phases**
- Bypassing or modifying Windows authentication, `winlogon`, or credential providers.
- Storing passwords, PINs, or credentials of any kind.
- Sending biometric data off the machine.
