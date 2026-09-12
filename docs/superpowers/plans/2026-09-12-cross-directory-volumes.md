# Cross-directory volume correction

> **For agentic workers:** Use superpowers:subagent-driven-development to implement and review each task.

**Goal:** Detect complementary archive volumes spread over imported folders, collect them beside the entry volume after validation, and restore their original names and locations on request.

**Architecture:** Keep discovery read-only and confined to imported paths. Match normalized volume families across that scope, require unambiguous numbering, validate through isolated aliases, then use a journaled same-volume move. Preserve completed tasks and journal identity when adding further inputs.

**Tech stack:** Existing C#/.NET 10 WPF application and pinned 7-Zip engine.

## Constraints

- No arbitrary parent-directory expansion; importing the common parent or all relevant folders supplies the search scope.
- Never merge duplicate independent sets automatically or overwrite existing files.
- Consolidate into the entry volume directory; retain SHA-256 identities and original paths in the common-parent journal.
- Preserve legacy journal recovery and reject cross-drive or drive-root-wide consolidation.
- User files, copied samples, passwords and private validation output remain outside version control and distribution.

## Tasks

- [x] Scanner: reproduce `.part1.exe`/`.part2.rar` mismatch and sibling-folder separation with public fixtures; normalize names and merge complementary scoped families; verify duplicates, missing volumes and ZIP/RAR anchors.
- [x] Transactions and engine: test safe multi-folder collection, original-path restoration, conflicts, interruption and retry; add constrained journal metadata; validate SFX physical sizes including verified executable offset; stop recursion when any member is under a software subtree.
- [x] Desktop flow: reproduce importing folder 1 followed by folder 2; rescan still-present pending members and replace only fully covered growing groups while retaining inclusion choices; accept manually grouped cross-folder volumes; show source folders and destination clearly.
- [ ] Verification and delivery: run full core/engine and desktop suites, build portable v0.1.1, verify the real sample's read-only grouping and listing, update instructions, publish main/tag, and download and verify the Release.

Regression commands: `.tools/dotnet/dotnet.exe run --project tests/AutoExtractor.Tests -c Release` and `.tools/dotnet/dotnet.exe run --project tests/AutoExtractor.App.SmokeTests -c Release`. Packaging: `scripts/build.ps1 -DotNet D:\Code\AutoExtractor\.tools\dotnet\dotnet.exe`.
