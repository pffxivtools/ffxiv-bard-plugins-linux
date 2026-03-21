using System.Diagnostics;
using Xunit.Abstractions;

namespace XivIpc.Tests;

/// <summary>
/// Test class that adds timing logs for test startup and teardown.
/// Inherit from this class to get timing information for your tests.
/// </summary>
public abstract class TimedTestBase
{
    private readonly Stopwatch _testStopwatch = new();
    private readonly ITestOutputHelper? _output;

    protected TimedTestBase(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    protected void LogTestStart(string testName)
    {
        _testStopwatch.Restart();
        Log($"TEST START: {testName}");
    }

    protected void LogTestEnd(string testName, string context = "")
    {
        _testStopwatch.Stop();
        var contextSuffix = string.IsNullOrEmpty(context) ? "" : $" ({context})";
        Log($"TEST END: {testName}{contextSuffix} - Duration: {_testStopwatch.Elapsed:mm\\:ss\\.fff}");
    }

    protected void Log(string message)
    {
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
        var logMessage = $"[{timestamp}] {message}";
        
        _output?. WriteLine(logMessage);
        Console.WriteLine(logMessage);
    }

    protected void LogStep(string stepName)
    {
        var elapsed = _testStopwatch.Elapsed;
        Log($"STEP: {stepName} (elapsed: {elapsed:mm\\:ss\\.fff})");
    }
}
