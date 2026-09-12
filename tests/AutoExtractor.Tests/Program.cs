using AutoExtractor.Tests;
CoreTests.Register();
EngineTests.Register();
RarFixtureTests.Register();
ArchiveCompatibilityTests.Register();
return await TestRunner.RunAsync();
