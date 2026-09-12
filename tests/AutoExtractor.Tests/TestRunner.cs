namespace AutoExtractor.Tests;
public static class TestRunner
{
    public static List<(string Name, Func<Task> Run)> Cases { get; } = [];
    public static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    public static async Task<int> RunAsync() { int failures = 0; foreach (var test in Cases) { try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); } catch(Exception e) { failures++; Console.WriteLine($"FAIL {test.Name}: {e}"); } } Console.WriteLine($"{Cases.Count - failures}/{Cases.Count} passed"); return failures == 0 ? 0 : 1; }
}
