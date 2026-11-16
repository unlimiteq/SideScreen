# Contributing to SideScreen

Thanks for helping out! Bug reports, device test results, documentation fixes and code are all welcome.

## Reporting bugs

Use the **Bug report** issue template and include:

- Windows version, GPU and driver version
- Tablet model and Android version
- What you expected and what happened
- The log: click **Open log** in the SideScreen window (`%LOCALAPPDATA%\SideScreen\log.txt`)

Please check the log for anything you'd rather not share before attaching it.

## Device reports

Tried SideScreen on a tablet or phone that is not in the compatibility table? Open a **Device report** issue — even "doesn't work" results are useful.

## Development setup

See [Building from source](README.md#building-from-source). `.\build.ps1` builds all three parts into `dist\`.

Useful while developing:

- `dist\SideScreen.exe --start` starts streaming immediately.
- Tablet-side logs: `adb logcat -s SideScreen`.
- `capture\build\sscapture.exe --adapter 0 --output 0 --fps 120 --mbps 80 > out.bin` runs the capture helper on its own; it prints one `stat` line per second to stderr.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep changes focused; one feature or fix per pull request.
3. Follow the existing style (see `.editorconfig`): 4-space indentation, no new dependencies unless they are really needed.
4. Make sure `.\build.ps1` succeeds and test on a real tablet when you touch the streaming path.
5. Update `README.md` and `CHANGELOG.md` (under *Unreleased*) when behaviour changes.
6. Fill in the pull request template.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md). By participating you agree to uphold it.
