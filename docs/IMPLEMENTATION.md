# AutoExtractor v0.1.0

Windows 10/11 x64 offline Chinese WPF portable application. C# .NET 10, bundled full 7-Zip engine. Public repository peterL1244/AutoExtractor on main, MIT original code. Passwords remain in memory only. User explicitly approved in-place restoration of archive extensions, with durable rename journals and recovery; never overwrite or delete original downloads. Only read self-extracting EXEs as archives; never execute user programs.

## Deliverables
- Detect ZIP/7z/RAR by bytes/structure even under jpg/mp4/tmp/exe extensions; group numbered split files, RAR part/legacy and ZIP z01 volumes. Ambiguous groups require manual selection/order. Validate groups with engine before renaming.
- Drag files/folders, scan preview, output picker, task queue, password prompt/retry, progress/cancel/retry, open output, restore names and manual continuation.
- Extract into isolated staging folders, check archive paths and links, disk space, retain intermediate layers. Smart-stop directories with normal executable and companion assets. Recursion limit 10 and content repeat guard. Default output sibling AutoExtractor_Output, isolated folder per archive group. Skip Office/JAR/APK/resource containers.
- Windows self-contained portable ZIP with bundled engine, docs/license, Actions verification/build/release v0.1.0. No user files/passwords/journals published.

## Verification
Generated fixtures: three layer exe -> numbered volumes -> tmp 7z -> final text; different nested passwords and encrypted headers; disguised ZIP volumes; mixed real media/exe; missing/truncated/duplicate volumes; rename conflict and rollback hashes; Chinese/long paths; traversal and links; cancellation; smart stopping; isolated-runtime launch. RAR test fixtures need explicit redistributable origin or documented unverified combinations.

## Components
Core Contracts.cs owns shared data/interfaces. Scanner and RenameService implement core discovery and transactional restore. Engine SevenZipEngine implements IArchiveEngine. ExtractionCoordinator orchestrates scanner, rename service, engine with async password callback. WPF stays thin and responsive. Tests use a dependency-free console harness with real archives and fail exit codes.
