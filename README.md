# Mosaic Studio

Mosaic Studio is a native Windows desktop MVP for arranging local video feeds, previewing a selected webcam or video, and switching between a studio workspace and an audience-style feed selector.

## macOS companion

The `Mac` folder contains a separate native SwiftUI companion for macOS 13 and later. Open `Mac/MosaicStudioMac.xcodeproj` in Xcode, select the **MosaicStudioMac** scheme, and run it. Grant camera and microphone access when macOS prompts. Install FFmpeg with `brew install ffmpeg` and configure an RTMP destination before broadcasting. The Mac app stores stream keys in the macOS Keychain rather than sharing Windows' DPAPI-encrypted settings; studio layouts are stored separately in `~/Library/Application Support/MosaicStudioMac`. FFmpeg receives a destination's stream key as a process argument while that broadcast is running.

The Mac companion includes camera and local-video feeds, a local audience preview, optional microphone audio, and simultaneous RTMP output. Camera/audio capture indices are matched against FFmpeg's AVFoundation device list. The Watch view is local only; screen capture and remote/public viewer hosting are not included.

## Build installers

Installer outputs are written to the ignored `artifacts/installers` folder. Each installer must be built on its target operating system.

**Windows:** Install Inno Setup 6, then run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Packaging\Windows\build-installer.ps1` from the repository root. The script publishes a self-contained x64 app and creates `MosaicStudio-Windows-Setup-1.0.0.exe`. The installer is per-user and does not delete studio settings when uninstalled. It is unsigned, so Windows SmartScreen may show a warning. FFmpeg is a separate prerequisite; install it with `winget install --id BtbN.FFmpeg.LGPL.8.1 --exact`.

**macOS:** On a Mac with Xcode and the command-line tools installed, run `bash Mac/build-installer.sh`. This builds the SwiftUI app and creates both `MosaicStudio-macOS-1.0.0.pkg` (installer package) and `MosaicStudio-macOS-1.0.0.dmg` (drag-to-Applications disk image). Install FFmpeg separately with `brew install ffmpeg`. For public distribution, sign the app and installer with Apple Developer ID certificates and notarize them; unsigned local builds are intended for testing and may trigger macOS security warnings.

## Run

Install the .NET 9 SDK and Windows Desktop Runtime, then run:

```powershell
dotnet run --project .\LivestreamStudio.csproj
```

To build the Windows application:

```powershell
dotnet build .\LivestreamStudio.csproj -c Release
```

Choose **Refresh** to discover connected Windows cameras and microphones. Select a camera and choose **Add camera** to preview its live feed; select a camera feed and use the preview playback button to pause or resume it. You can also import and preview a local video. Both source types are available to select in the audience view.

To stream, set up the YouTube/Twitch destination or add a custom RTMP destination. Enter the RTMP server address without its stream key. Keys are protected with Windows DPAPI for the current Windows account and are stored encrypted in `%LOCALAPPDATA%\MosaicStudio\studio.json`. Select one or more configured destinations, choose a camera or video feed, and press **Start broadcast**; the button becomes **Stop broadcast** while FFmpeg sends one encoded H.264/AAC stream to all selected RTMP endpoints. Camera audio is optional and disabled until explicitly enabled.

FFmpeg must be installed; on this Windows machine, install the LGPL release with `winget install --id BtbN.FFmpeg.LGPL.8.1 --exact`. The broadcast uses a 720p H.264 stream at a 4.5 Mbps target bitrate and AAC audio. Broadcasting with a camera temporarily pauses its local preview until the broadcast stops, because the Windows camera is opened directly by the encoder.

The application stores its studio setup in `%LOCALAPPDATA%\MosaicStudio\studio.json`. Saved camera feeds reconnect using their Windows camera device path; imported feeds refer to their original video files, which are not copied into the project. Camera and microphone use requires Windows privacy permission.

## MVP limits

RTMP publishing requires the endpoint and current stream key from each streaming platform; Mosaic Studio does not sign in to platform accounts or validate keys before connecting. Keep your stream keys private. The Watch view is a local feed selector, not a hosted public viewer page; screen capture and remote viewer connections are not implemented.
