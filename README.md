# Palm Rejector Pro

A fullscreen transparent Windows overlay that detects and rejects large touch contacts (palms/resting hands) on capacitive touchscreens.

## Features

- Fullscreen transparent overlay
- Real-time palm detection
- Adjustable touch threshold
- System tray integration
- Startup with Windows support
- Saved settings
- Live touch diagnostics

## Default Palm Threshold

45px

Recommended:
- 10-25px = fingertip
- 30-45px = large finger
- 45px+ = palm

## Controls

- UP Arrow = Increase threshold
- DOWN Arrow = Decrease threshold
- ESC = Exit

## Tray Menu

Right-click tray icon:
- Enable Overlay
- Disable Overlay
- Recalibrate
- Start With Windows
- Exit

## Install .NET 8

https://dotnet.microsoft.com/en-us/download/dotnet/8.0

## Build

dotnet build

## Run

dotnet run

## Publish Standalone EXE

dotnet publish -c Release -r win-x64 --self-contained true

Standalone EXE path:

bin/Release/net8.0-windows/win-x64/publish/

## Important

This is the closest possible user-mode solution on Windows without writing a signed HID filter driver.

It works best on touchscreens that expose:
- contact area
- touch geometry
- pointer size

through the Windows Pointer API.

## Persistent Blob Architecture

Palm rejection now uses persistent `PalmBlob` tracking:

- large contact seeds a blob (`InnerMask`)
- each blob also owns an expanded sensing zone (`OuterRing`)
- touches inside `OuterRing` merge/expand the blob
- touches inside `InnerMask` are rejected
- live blobs decay over time and are removed automatically

`WM_NCHITTEST` now returns `HTCLIENT` so the overlay receives `WM_POINTER*` events reliably while remaining visually transparent via `BackColor`/`TransparencyKey`.

## Calibration (Onboarding / Recalibration)

On first run (or when choosing **Recalibrate** from tray menu), the app enters onboarding mode and captures palm-size samples to estimate initial palm-reject surface thresholds.

## Blob Settings

These are saved in settings:

- `PalmPadding` (default `40`)
- `OuterRingSize` (default `60`)
- `BlobDecayRate` (default `0.08`)
- `BlobDecayIntervalMs` (default `150`)
