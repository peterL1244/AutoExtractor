# Third-party notices

## 7-Zip 26.03

Copyright (C) 1999-2026 Igor Pavlov. Unmodified Windows x64 `7z.exe` and `7z.dll` extracted from the official 26.03 distribution. The original package URL and binary SHA-256 values are in `vendor/7zip/manifest.json` (portable package: `tools/7zip/manifest.json`).

7-Zip uses GNU LGPL with additional unRAR restrictions for RAR components, and BSD/public-domain portions. Full component notices: `vendor/7zip/License.txt`; LGPL text: `vendor/7zip/copying.txt`. In the portable package these files are in `tools/7zip/`.

Corresponding source: https://github.com/ip7z/7zip/tree/26.03 and https://github.com/ip7z/7zip/releases/tag/26.03 . AutoExtractor does not modify the engine. Do not use the unRAR portions to develop a RAR-compatible compressor.

## .NET runtime

Self-contained releases include the Microsoft .NET runtime under its supplied license. Runtime copyright and third-party notices are included by the .NET publishing SDK. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf .

## libarchive test fixtures

RAR fixtures, if present under `tests/fixtures/libarchive`, are from the pinned libarchive revision documented in that directory. Their own COPYING and upstream test-source notices are retained there. These fixtures are development-only and are not included in the portable application.
