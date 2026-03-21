using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;

return await ProgramMainAsync(args).ConfigureAwait(false);

static async Task<int> ProgramMainAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: XivIpc.WineTestHost <command> <channel> [args]");
        return 2;
    }

    string command = args[0].Trim().ToLowerInvariant();
    string channel = args[1];
    int maxPayloadBytes = ResolveMaxPayloadBytes();

    try
    {
        return command switch
        {
            "connect" => await ConnectAsync(channel, maxPayloadBytes).ConfigureAwait(false),
            "connect-dispose-and-wait" => await ConnectDisposeAndWaitAsync(channel, maxPayloadBytes, args).ConfigureAwait(false),
            "hold" => await HoldAsync(channel, maxPayloadBytes, args).ConfigureAwait(false),
            "publish" => await PublishAsync(channel, maxPayloadBytes, args).ConfigureAwait(false),
            "subscribe-many" => await SubscribeManyAsync(channel, maxPayloadBytes, args).ConfigureAwait(false),
            "subscribe-once" => await SubscribeOnceAsync(channel, maxPayloadBytes, args).ConfigureAwait(false),
            _ => Fail($"Unknown command '{command}'.")
        };
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

static async Task<int> ConnectAsync(string channel, int maxPayloadBytes)
{
    using var bus = CreateBus(channel, maxPayloadBytes);
    Console.Out.WriteLine("CONNECTED");
    await Console.Out.FlushAsync().ConfigureAwait(false);
    return 0;
}

static async Task<int> HoldAsync(string channel, int maxPayloadBytes, string[] args)
{
    if (args.Length < 3 || !int.TryParse(args[2], out int holdSeconds) || holdSeconds <= 0)
        return Fail("hold requires a positive number of seconds.");

    using var bus = CreateBus(channel, maxPayloadBytes);
    Console.Out.WriteLine("CONNECTED");
    await Console.Out.FlushAsync().ConfigureAwait(false);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds)).ConfigureAwait(false);
    return 0;
}

static async Task<int> ConnectDisposeAndWaitAsync(string channel, int maxPayloadBytes, string[] args)
{
    if (args.Length < 3 || !int.TryParse(args[2], out int waitMs) || waitMs < 0)
        return Fail("connect-dispose-and-wait requires a non-negative wait time in milliseconds.");

    var bus = CreateBus(channel, maxPayloadBytes);
    Console.Out.WriteLine("CONNECTED");
    await Console.Out.FlushAsync().ConfigureAwait(false);
    bus.Dispose();
    Console.Out.WriteLine("DISPOSED");
    await Console.Out.FlushAsync().ConfigureAwait(false);
    await Task.Delay(waitMs).ConfigureAwait(false);
    return 0;
}

static async Task<int> PublishAsync(string channel, int maxPayloadBytes, string[] args)
{
    if (args.Length < 3)
        return Fail("publish requires a base64 payload argument.");

    byte[] payload = Convert.FromBase64String(args[2]);
    using var bus = CreateBus(channel, Math.Max(maxPayloadBytes, payload.Length));
    await Task.Delay(750).ConfigureAwait(false);
    await bus.PublishAsync(payload).ConfigureAwait(false);
    Console.Out.WriteLine("PUBLISHED");
    await Console.Out.FlushAsync().ConfigureAwait(false);
    return 0;
}

static async Task<int> SubscribeOnceAsync(string channel, int maxPayloadBytes, string[] args)
{
    int timeoutMs = 15_000;
    if (args.Length >= 3 && (!int.TryParse(args[2], out timeoutMs) || timeoutMs <= 0))
        return Fail("subscribe-once timeout must be a positive integer.");

    using var bus = CreateBus(channel, maxPayloadBytes);
    using var cts = new CancellationTokenSource(timeoutMs);

    Console.Out.WriteLine("CONNECTED");
    await Console.Out.FlushAsync().ConfigureAwait(false);

    await foreach (var item in bus.SubscribeAsync(cts.Token).ConfigureAwait(false))
    {
        Console.Out.WriteLine("MESSAGE:" + Convert.ToBase64String(GetBytes(item)));
        await Console.Out.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    return Fail("subscribe-once completed without receiving a message.");
}

static async Task<int> SubscribeManyAsync(string channel, int maxPayloadBytes, string[] args)
{
    if (args.Length < 3 || !int.TryParse(args[2], out int expectedCount) || expectedCount <= 0)
        return Fail("subscribe-many requires a positive expected message count.");

    int timeoutMs = 15_000;
    if (args.Length >= 4 && (!int.TryParse(args[3], out timeoutMs) || timeoutMs <= 0))
        return Fail("subscribe-many timeout must be a positive integer.");

    using var bus = CreateBus(channel, maxPayloadBytes);
    using var cts = new CancellationTokenSource(timeoutMs);
    int observed = 0;

    Console.Out.WriteLine("CONNECTED");
    await Console.Out.FlushAsync().ConfigureAwait(false);

    await foreach (var item in bus.SubscribeAsync(cts.Token).ConfigureAwait(false))
    {
        Console.Out.WriteLine("MESSAGE:" + Convert.ToBase64String(GetBytes(item)));
        await Console.Out.FlushAsync().ConfigureAwait(false);
        observed++;

        if (observed >= expectedCount)
            return 0;
    }

    return Fail($"subscribe-many completed after observing {observed} of {expectedCount} expected messages.");
}

static TinyMessageBus CreateBus(string channel, int maxPayloadBytes)
    => new(new TinyMemoryMappedFile(channel, maxPayloadBytes), disposeFile: true);

static int ResolveMaxPayloadBytes()
{
    string? raw = Environment.GetEnvironmentVariable("TINYIPC_TEST_MAX_PAYLOAD_BYTES");
    return int.TryParse(raw, out int value) && value > 0 ? value : 4096;
}

static byte[] GetBytes(object item)
{
    if (item is BinaryData data)
        return data.ToArray();

    if (item is TinyMessageReceivedEventArgs e)
        return e.Message is byte[] bytes ? bytes : e.Message.ToArray();

    if (item is byte[] raw)
        return raw;

    if (item is IReadOnlyList<byte> list)
        return list is byte[] arr ? arr : list.ToArray();

    return Encoding.UTF8.GetBytes(item.ToString() ?? string.Empty);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
