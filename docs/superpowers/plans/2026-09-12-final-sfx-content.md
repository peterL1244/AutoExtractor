# Final self-extracting archive and content directory

**Goal:** Continue through a numbered standalone self-extracting archive and present the real payload directory, preserving the original files and format-specific software layout.

- Reproduce a complete encrypted `download3397.exe` being mistaken for volume 3397. Distinguish loose numeric download identifiers from explicit volume syntax and credible continuation members.
- Preserve ordinary software resources and platform launchers, including PCK and ELF files; keep genuine split archives actionable.
- Test nested encrypted SFX extraction to both a Ren'Py-style payload and an EXE/PCK/platform-launcher payload using generated, public test data only.
- Expose one unambiguous content directory for browsing, skip single-directory wrappers without flattening files, and retain the complete output location when results are multiple or incomplete.
- Verify the actual remaining encrypted archive locally using the user-supplied password in memory. Preserve its source identity; keep user data and passwords outside the repository and release.
- Run all automated and desktop tests, package v0.1.2, publish the fix, and verify the release asset.
