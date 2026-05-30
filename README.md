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

## Persistent Blob Architecture Update

This build now implements:

- persistent support-contact territory tracking
- inner and outer adaptive masks
- blob reacquisition logic
- slow confidence decay
- multi-contact absorption
- exterior touch-safe interaction zones
- drawing-oriented support-contact persistence

The system is optimized for:
- stylus workflows
- resting palm interaction
- adaptive arm/palm tracking
- universal capacitive touchscreens
