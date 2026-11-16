# Third-party notices

SideScreen includes or redistributes the following components. Each remains under its own license.

## Virtual Display Driver (MttVDD)

- Files: `driver/MttVDD.dll`, `driver/MttVDD.inf`, `driver/mttvdd.cat` (unmodified, signed release 25.7.23)
- Source: https://github.com/VirtualDrivers/Virtual-Display-Driver
- License: MIT — see the project's LICENSE file.

`driver/vdd_settings.xml` is SideScreen's own mode list for the driver.

## NVIDIA Video Codec SDK header

- File: `capture/third_party/nvEncodeAPI.h` (via https://github.com/FFmpeg/nv-codec-headers, tag n12.1.14.0)
- Copyright (c) 2010-2023 NVIDIA Corporation
- License: MIT — the full license text is at the top of the header file.

SideScreen loads `nvEncodeAPI64.dll` from the installed NVIDIA driver at run time; no NVIDIA binaries are redistributed.

## Android SDK Platform-Tools (adb)

- Files in release packages: `adb/adb.exe`, `adb/AdbWinApi.dll`, `adb/AdbWinUsbApi.dll` (unmodified)
- Source: https://developer.android.com/tools/releases/platform-tools (Android Open Source Project)
- adb is built from AOSP sources licensed under the Apache License 2.0; the binaries are distributed by
  Google under the Android Software Development Kit License Agreement. They are not part of this
  repository's source tree.
