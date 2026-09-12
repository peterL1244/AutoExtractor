using AutoExtractor.Tests;
CoreTests.Register();
EngineTests.Register();
RarFixtureTests.Register();
ArchiveCompatibilityTests.Register();
CrossDirectoryScannerTests.Register();
CrossDirectoryRenameTests.Register();
CrossDirectoryEngineTests.Register();
return await TestRunner.RunAsync();
