using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TinyIpc.IO;
using TinyIpc.Messaging;
using XivIpc.Internal;
using XivIpc.Messaging;
using Xunit;

namespace XivIpc.Tests;

[Collection("TinyIpc Serial")]
public sealed class SidecarMultiUserTests
{
    public static IEnumerable<object[]> Modes()
    {
        yield return new object[] { "sidecar" };
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task SecondaryUnixUser_CanConnect_ReadRing_AndReceivePublish_WhenEnabled(string mode)
    {
        if (!RuntimeHarness.ShouldRun())
            return;

        using TestEnvironmentScope scope = new(mode);
        using var publisher = scope.CreateBus();
        await scope.WaitForLiveSocketAsync();

        string subscriberScriptPath = scope.CreateSubscriberScript();
        using HostedProcess subscriber = RuntimeHarness.StartSecondaryUserProcess(
            scope.SecondaryUser,
            "python3",
            [subscriberScriptPath, scope.SocketPath, scope.ChannelName]);

        await subscriber.WaitForStdoutLineAsync("CONNECTED", TimeSpan.FromSeconds(15));
        string ringLine = await subscriber.WaitForStdoutPrefixAsync("RING:", TimeSpan.FromSeconds(15));
        string ringPath = ringLine["RING:".Length..];
        string statePath = UnixSidecarProcessManager.ResolveBrokerStatePathForDiagnostics();
        Assert.Equal($"{scope.SharedGroup} 2770", GetStat(scope.SharedDirectory, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(scope.SocketPath, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(ringPath, "%G %a"));
        Assert.Equal($"{scope.SharedGroup} 660", GetStat(statePath, "%G %a"));

        byte[] payload = Encoding.UTF8.GetBytes("multi-user-sidecar");
        await publisher.PublishAsync(payload);

        string messageLine = await subscriber.WaitForStdoutPrefixAsync("MESSAGE:", TimeSpan.FromSeconds(15));
        Assert.Equal(Convert.ToBase64String(payload), messageLine["MESSAGE:".Length..]);
        Assert.Equal(0, await subscriber.WaitForExitAsync(TimeSpan.FromSeconds(10)));

        publisher.Dispose();
        await scope.WaitForBrokerShutdownAsync();

        Assert.False(File.Exists(scope.SocketPath));
        Assert.False(File.Exists(ringPath));
    }

    private static string GetStat(string path, string format)
    {
        using var process = Process.Start(new ProcessStartInfo("stat")
        {
            ArgumentList = { "-c", format, path },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to start stat.");

        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"stat failed for '{path}': {error}");

        return output;
    }

    private sealed class TestEnvironmentScope : IDisposable
    {
        private readonly IDisposable _overrides;

        public TestEnvironmentScope(string mode)
        {
            SecondaryUser = TinyIpcEnvironment.GetEnvironmentVariable("TINYIPC_MULTIUSER_SECONDARY_USER")
                ?? throw new InvalidOperationException("TINYIPC_MULTIUSER_SECONDARY_USER must be set for multi-user tests.");
            SharedGroup = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.SharedGroup)
                ?? throw new InvalidOperationException("TINYIPC_SHARED_GROUP must be set for multi-user tests.");

            ChannelName = $"xivipc-multiuser-{Guid.NewGuid():N}";
            SharedDirectory = Path.Combine(Path.GetTempPath(), "xivipc-multiuser-tests", Guid.NewGuid().ToString("N"));
            SocketPath = Path.Combine(SharedDirectory, "tinyipc-sidecar.sock");

            Directory.CreateDirectory(SharedDirectory);

            ProductionPathTestEnvironment.ResetLogger();
            var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (ProductionPathTestEnvironment.IsProductionPath(mode))
            {
                ProductionPathTestEnvironment.PrepareStagedNativeHost(SharedDirectory);
                overrides[TinyIpcEnvironment.MessageBusBackend] = null;
                overrides[TinyIpcEnvironment.NativeHostPath] = null;
                overrides[TinyIpcEnvironment.SharedDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory);
                overrides[TinyIpcEnvironment.SharedGroup] = SharedGroup;
                overrides[TinyIpcEnvironment.LogDirectory] = ProductionPathTestEnvironment.ToWindowsStylePath(SharedDirectory);
                overrides[TinyIpcEnvironment.LogLevel] = "info";
                overrides[TinyIpcEnvironment.EnableLogging] = "1";
                overrides[TinyIpcEnvironment.FileNotifier] = "auto";
                overrides[TinyIpcEnvironment.BrokerIdleShutdownMs] = "2000";
            }
            else
            {
                overrides[TinyIpcEnvironment.MessageBusBackend] = "sidecar";
                overrides[TinyIpcEnvironment.SharedDirectory] = SharedDirectory;
                overrides[TinyIpcEnvironment.SharedGroup] = SharedGroup;
                overrides[TinyIpcEnvironment.NativeHostPath] = UnixSidecarProcessManager.ResolveNativeHostPath();
                overrides[TinyIpcEnvironment.BrokerIdleShutdownMs] = "2000";
            }

            if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.UnixShell)))
                overrides[TinyIpcEnvironment.UnixShell] = "/bin/sh";

            _overrides = TinyIpcEnvironment.Override(overrides);
        }

        public string ChannelName { get; }
        public string SecondaryUser { get; }
        public string SharedDirectory { get; }
        public string SharedGroup { get; }
        public string SocketPath { get; }

        public TinyMessageBus CreateBus(int maxPayloadBytes = 4096)
            => new(new TinyMemoryMappedFile(ChannelName, maxPayloadBytes), disposeFile: true);

        public async Task WaitForLiveSocketAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            Exception? last = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(SocketPath))
                    {
                        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                        socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"Timed out waiting for live broker socket '{SocketPath}'.", last);
        }

        public async Task WaitForBrokerShutdownAsync()
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (!File.Exists(SocketPath))
                    return;

                await Task.Delay(50);
            }

            Assert.False(File.Exists(SocketPath), $"Expected broker socket '{SocketPath}' to be removed.");
        }

        public string CreateSubscriberScript()
        {
            string scriptPath = Path.Combine(SharedDirectory, "sidecar_secondary_subscriber.py");
            File.WriteAllText(scriptPath, PythonSubscriberScript);
            return scriptPath;
        }

        public void Dispose()
        {
            _overrides.Dispose();
            ProductionPathTestEnvironment.ResetLogger();

            try
            {
                if (Directory.Exists(SharedDirectory))
                    Directory.Delete(SharedDirectory, recursive: true);
            }
            catch
            {
            }
        }

    }

    private sealed class HostedProcess : IDisposable
    {
        private readonly Process _process;
        private readonly BlockingCollection<string> _stdoutLines = new();
        private readonly BlockingCollection<string> _stderrLines = new();
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;

        public HostedProcess(Process process)
        {
            _process = process;
            _stdoutPump = Task.Run(() => PumpAsync(process.StandardOutput, _stdoutLines));
            _stderrPump = Task.Run(() => PumpAsync(process.StandardError, _stderrLines));
        }

        public async Task<string> WaitForStdoutLineAsync(string expected, TimeSpan timeout)
        {
            string line = await WaitForStdoutPrefixAsync(expected, timeout);
            if (!string.Equals(line, expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected stdout line '{expected}' but received '{line}'.");

            return line;
        }

        public async Task<string> WaitForStdoutPrefixAsync(string prefix, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (_stdoutLines.TryTake(out string? line, millisecondsTimeout: 100))
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return line;
                }

                if (_process.HasExited && _stdoutLines.Count == 0)
                    break;
            }

            throw new TimeoutException(
                $"Timed out waiting for stdout prefix '{prefix}'. stderr={string.Join(Environment.NewLine, _stderrLines.ToArray())}");
        }

        public async Task<int> WaitForExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(_stdoutPump, _stderrPump);
            return _process.ExitCode;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try { _process.Dispose(); } catch { }
            _stdoutLines.Dispose();
            _stderrLines.Dispose();
        }

        private static async Task PumpAsync(StreamReader reader, BlockingCollection<string> sink)
        {
            try
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line is null)
                        break;

                    sink.Add(line);
                }
            }
            finally
            {
                sink.CompleteAdding();
            }
        }
    }

    private static class RuntimeHarness
    {
        public static bool ShouldRun()
        {
            string? raw = Environment.GetEnvironmentVariable("TINYIPC_ENABLE_MULTIUSER_TESTS");
            bool enabled = string.Equals(raw, "1", StringComparison.Ordinal)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);

            if (!enabled || !OperatingSystem.IsLinux())
                return false;

            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TINYIPC_MULTIUSER_SECONDARY_USER"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP"));
        }

        public static HostedProcess StartSecondaryUserProcess(string username, string executable, IReadOnlyList<string> arguments)
        {
            ProcessStartInfo psi = new("sudo")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(username);
            psi.ArgumentList.Add(executable);

            foreach (string argument in arguments)
                psi.ArgumentList.Add(argument);

            Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start secondary-user process.");
            return new HostedProcess(process);
        }
    }

    private const string PythonSubscriberScript = """
import base64
import mmap
import os
import socket
import struct
import sys

HEADER_HEAD_SEQ_OFFSET = 16
HEADER_TAIL_SEQ_OFFSET = 24
HEADER_SIZE = 64
SLOT_MAGIC = 0x54494253
SLOT_MAGIC_OFFSET = 0
SLOT_PAYLOAD_LEN_OFFSET = 4
SLOT_SEQUENCE_OFFSET = 8
SLOT_SENDER_SESSION_OFFSET = 24
SLOT_HEADER_SIZE = 40

FRAME_HELLO = 1
FRAME_NOTIFY = 5
FRAME_ATTACH_RING = 10
FRAME_READY = 11
FRAME_ERROR = 13

channel = sys.argv[2]
socket_path = sys.argv[1]

def send_frame(sock, frame_type, payload):
    sock.sendall(struct.pack("<I", 1 + len(payload)) + bytes([frame_type]) + payload)

def recv_exact(sock, length):
    data = bytearray()
    while len(data) < length:
        chunk = sock.recv(length - len(data))
        if not chunk:
            raise EOFError()
        data.extend(chunk)
    return bytes(data)

def read_frame(sock):
    length = struct.unpack("<I", recv_exact(sock, 4))[0]
    data = recv_exact(sock, length)
    return data[0], data[1:]

channel_bytes = channel.encode("utf-8")
hello_payload = struct.pack("<iiiiiii", 3, os.getpid(), 4096, 500, 15000, 1, len(channel_bytes)) + channel_bytes

with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
    sock.connect(socket_path)
    send_frame(sock, FRAME_HELLO, hello_payload)

    frame_type, payload = read_frame(sock)
    if frame_type == FRAME_ERROR:
        raise RuntimeError(payload.decode("utf-8"))
    if frame_type != FRAME_ATTACH_RING:
        raise RuntimeError(f"unexpected attach frame {frame_type}")

    version, path_len, slot_count, slot_payload_bytes = struct.unpack_from("<iiii", payload, 0)
    start_sequence = struct.unpack_from("<q", payload, 16)[0]
    session_id = struct.unpack_from("<q", payload, 24)[0]
    ring_path = payload[36:36 + path_len].decode("utf-8")

    print("CONNECTED", flush=True)
    print("RING:" + ring_path, flush=True)

    fd = os.open(ring_path, os.O_RDWR)
    try:
        ring_length = 64 + slot_count * (SLOT_HEADER_SIZE + slot_payload_bytes)
        mm = mmap.mmap(fd, ring_length, access=mmap.ACCESS_WRITE)
        try:
            frame_type, _ = read_frame(sock)
            if frame_type != FRAME_READY:
                raise RuntimeError(f"unexpected ready frame {frame_type}")

            while True:
                frame_type, payload = read_frame(sock)
                if frame_type == FRAME_NOTIFY:
                    head = struct.unpack_from("<q", mm, HEADER_HEAD_SEQ_OFFSET)[0]
                    tail = struct.unpack_from("<q", mm, HEADER_TAIL_SEQ_OFFSET)[0]
                    seq = start_sequence if start_sequence >= tail else tail

                    while seq < head:
                        slot_offset = HEADER_SIZE + (seq % slot_count) * (SLOT_HEADER_SIZE + slot_payload_bytes)
                        slot_magic = struct.unpack_from("<I", mm, slot_offset + SLOT_MAGIC_OFFSET)[0]
                        payload_len = struct.unpack_from("<i", mm, slot_offset + SLOT_PAYLOAD_LEN_OFFSET)[0]
                        slot_sequence = struct.unpack_from("<q", mm, slot_offset + SLOT_SEQUENCE_OFFSET)[0]
                        sender_session = struct.unpack_from("<q", mm, slot_offset + SLOT_SENDER_SESSION_OFFSET)[0]

                        if slot_magic == SLOT_MAGIC and slot_sequence == seq and 0 <= payload_len <= slot_payload_bytes and sender_session != session_id:
                            message = mm[slot_offset + SLOT_HEADER_SIZE: slot_offset + SLOT_HEADER_SIZE + payload_len]
                            print("MESSAGE:" + base64.b64encode(message).decode("ascii"), flush=True)
                            sys.exit(0)

                        seq += 1

                    start_sequence = head
                elif frame_type == FRAME_ERROR:
                    raise RuntimeError(payload.decode("utf-8"))
        finally:
            mm.close()
    finally:
        os.close(fd)
""";
}
