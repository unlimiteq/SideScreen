# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-15

First stable release.

### Added
- Extend mode: a real virtual second monitor (IddCx driver) at the tablet's native resolution and refresh rate, up to 2960 × 1848 @ 120 Hz.
- Mirror mode for any existing screen.
- GPU-only pipeline: Desktop Duplication → NVENC HEVC (ultra-low latency) → USB (adb reverse) → Android MediaCodec low-latency decode.
- Multi-touch from the tablet as real Windows touch input.
- Mouse pointer drawn as an overlay on the tablet.
- Options: resolution, tablet position (left/right/above/below), refresh rate, quality, touch on/off.
- Custom resolution and custom refresh rate (24–240 Hz); new modes are added to the virtual display driver on the fly, without admin rights.
- System tray app with status dot, Start/Stop menu and **Ctrl + Alt + S** hotkey.
- Optional notifications for connect, disconnect and unexpected stops.
- Connect automatically, start with Windows, close to tray; all settings remembered.
- One-click virtual display driver install.
- Tablet: connection banner, *Connection lost* screen with automatic reconnect, three-finger stats overlay.
- Automatic recovery from dropped USB tunnels, tablet app restarts, capture interruptions (lock screen / UAC) and PC stalls.
- "Open log" and "Check for updates" in the window footer.
- Command-line switches `--start`, `--mirror`, `--tray`, `--exit`.

[1.0.0]: https://github.com/unlimiteq/SideScreen/releases/tag/v1.0.0
