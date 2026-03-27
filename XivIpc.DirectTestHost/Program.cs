using System.Text;
using System.Reflection;
using TinyIpc.IO;
using TinyIpc.Messaging;

if (args.Length == 5)
{
    return await RunDirectReceiveAsync(args);
}

if ((args.Length == 6 || args.Length == 7) && string.Equals(args[0], "sidecar-receive", StringComparison.OrdinalIgnoreCase))
{
    return await RunSidecarReceiveAsync(args[1..]);
}

Console.Error.WriteLine("Usage:");
Console.Error.WriteLine("  XivIpc.DirectTestHost <channel> <maxPayloadBytes> <readyPath> <outputPath> <timeoutMs>");
Console.Error.WriteLine("  XivIpc.DirectTestHost sidecar-receive <channel> <maxPayloadBytes> <readyPath> <outputPath> <timeoutMs> [barrierPrefix]");
return 2;

static async Task<int> RunDirectReceiveAsync(string[] args)
{
    string channelName = args[0];
    if (!int.TryParse(args[1], out int maxPayloadBytes) || maxPayloadBytes <= 0)
    {
        Console.Error.WriteLine("maxPayloadBytes must be a positive integer.");
        return 2;
    }

    string readyPath = args[2];
    string outputPath = args[3];
    if (!int.TryParse(args[4], out int timeoutMs) || timeoutMs <= 0)
    {
        Console.Error.WriteLine("timeoutMs must be a positive integer.");
        return 2;
    }
    string? barrierPrefix = args.Length >= 6 ? args[5] : null;

    EnsureParentDirectory(readyPath);
    EnsureParentDirectory(outputPath);

    using var bus = new TinyMessageBus(new TinyMemoryMappedFile(channelName, maxPayloadBytes), disposeFile: true);
    var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

    bus.MessageReceived += (_, eventArgs) =>
    {
        received.TrySetResult(eventArgs.BinaryData.ToArray());
    };

    await File.WriteAllTextAsync(readyPath, "ready", Encoding.UTF8);

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
    try
    {
        byte[] payload = await received.Task.WaitAsync(cts.Token);
        await File.WriteAllBytesAsync(outputPath, payload, cts.Token);
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine($"Timed out waiting for direct shared-memory message on channel '{channelName}'.");
        return 1;
    }
}

static async Task<int> RunSidecarReceiveAsync(string[] args)
{
    string channelName = args[0];
    if (!int.TryParse(args[1], out int maxPayloadBytes) || maxPayloadBytes <= 0)
    {
        Console.Error.WriteLine("maxPayloadBytes must be a positive integer.");
        return 2;
    }

    string readyPath = args[2];
    string outputPath = args[3];
    if (!int.TryParse(args[4], out int timeoutMs) || timeoutMs <= 0)
    {
        Console.Error.WriteLine("timeoutMs must be a positive integer.");
        return 2;
    }
    string? barrierPrefix = args.Length >= 6 ? args[5] : null;

    EnsureParentDirectory(readyPath);
    EnsureParentDirectory(outputPath);

    using var bus = new TinyMessageBus(new TinyMemoryMappedFile(channelName, maxPayloadBytes), disposeFile: true);
    var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    bus.MessageReceived += (_, eventArgs) =>
    {
        byte[] payload = eventArgs.BinaryData.ToArray();
        string text = Encoding.UTF8.GetString(payload);
        if (!string.IsNullOrEmpty(barrierPrefix) && text.StartsWith(barrierPrefix, StringComparison.Ordinal))
        {
            File.WriteAllText(readyPath, "ready", Encoding.UTF8);
            return;
        }

        Console.WriteLine("MESSAGE:" + Convert.ToBase64String(payload));
        received.TrySetResult(payload);
    };

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
    try
    {
        await WaitForConnectedAsync(bus, TimeSpan.FromMilliseconds(timeoutMs));
        await File.WriteAllTextAsync(readyPath, "connected", Encoding.UTF8, cts.Token);

        byte[] payload = await received.Task.WaitAsync(cts.Token);
        await File.WriteAllBytesAsync(outputPath, payload, cts.Token);
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine($"Timed out waiting for sidecar message on channel '{channelName}'.");
        return 1;
    }
}

static void EnsureParentDirectory(string path)
{
    string? directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
}

static async Task WaitForConnectedAsync(TinyMessageBus bus, TimeSpan timeout)
{
    MethodInfo? method = typeof(TinyMessageBus).GetMethod(
        "WaitForConnectedForDiagnosticsAsync",
        BindingFlags.Instance | BindingFlags.NonPublic);

    if (method is null)
        throw new MissingMethodException(typeof(TinyMessageBus).FullName, "WaitForConnectedForDiagnosticsAsync");

    if (method.Invoke(bus, [timeout]) is not Task task)
        throw new InvalidOperationException("TinyMessageBus.WaitForConnectedForDiagnosticsAsync did not return a task.");

    await task.ConfigureAwait(false);
}
