using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XivIpc.Internal
{
    internal static class UnixSharedStorageHelpers
    {
        private static readonly TimeSpan LockWaitTimeout = TimeSpan.FromSeconds(5);
        private static readonly ConcurrentDictionary<string, object> ProcessGates =
            new(StringComparer.Ordinal);

        internal static void EnsureSharedDirectoryExists()
        {
            string directory = ResolveSharedDirectory();
            Directory.CreateDirectory(directory);
            ApplyPermissions(directory, isDirectory: true);

            TinyIpcLogger.Debug(
                nameof(UnixSharedStorageHelpers),
                "SharedDirectoryReady",
                "Ensured shared directory exists.",
                ("directory", directory));
        }

        internal static string GetSharedDirectoryPath()
        {
            return ResolveSharedDirectory();
        }

        internal static string BuildSharedFilePath(string name, string kind)
        {
            string directory = ResolveSharedDirectory();
            return Path.Combine(directory, BuildSharedFileName(name, kind));
        }

        internal static string BuildSharedObjectName(string name, string kind)
        {
            string scopeHash = ComputeStableHexHash(ResolveSharedDirectory());
            string objectName = BuildSharedFileName($"{name}_{scopeHash}", kind).Replace('.', '_');
            return "/" + objectName;
        }

        internal static string BuildNotifierSocketPath(string name, string uniqueSuffix)
        {
            string fileName = $"ti_n_{ComputeStableHexHash(name)}_{SanitizeSuffix(uniqueSuffix)}.sock";
            return Path.Combine(ResolveSharedDirectory(), fileName);
        }

        internal static string BuildNotifierSocketSearchPattern(string name)
        {
            return $"ti_n_{ComputeStableHexHash(name)}_*.sock";
        }

        internal static string BuildNotifierRegistrationPath(string name, string uniqueSuffix, string extension)
        {
            string fileName = $"ti_n_{ComputeStableHexHash(name)}_{SanitizeSuffix(uniqueSuffix)}.{extension}";
            return Path.Combine(ResolveSharedDirectory(), fileName);
        }

        internal static string BuildNotifierRegistrationSearchPattern(string name, string extension)
        {
            return $"ti_n_{ComputeStableHexHash(name)}_*.{extension}";
        }

        internal static string BuildWindowsEventName(string name, string uniqueSuffix)
        {
            string suffix = SanitizeSuffix(uniqueSuffix);
            string hash = ComputeStableHexHash(name);
            return $@"Local\TinyIpc_{hash}_{suffix}";
        }

        internal static object GetProcessGate(string path)
            => ProcessGates.GetOrAdd(path, static _ => new object());

        internal static void ApplyPermissions(string path, bool isDirectory)
        {
            RuntimeEnvironmentInfo runtime = RuntimeEnvironmentDetector.Detect();
            if (runtime.IsWindowsProcess)
            {
                TinyIpcLogger.Debug(
                    nameof(UnixSharedStorageHelpers),
                    "PermissionApplySkipped",
                    "Skipping Unix permission application for native Windows runtime.",
                    ("path", path),
                    ("isDirectory", isDirectory),
                    ("runtime", runtime.Kind));
                return;
            }

            string unixPath = ConvertPathToUnix(path);

            try
            {
                string? groupName = GetConfiguredGroup();
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    int gid = GetGroupId(groupName);
                    if (gid >= 0)
                    {
                        int chownResult = chown(unixPath, -1, gid);
                        if (chownResult != 0)
                        {
                            int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                            TinyIpcLogger.Warning(
                                nameof(UnixSharedStorageHelpers),
                                "SharedGroupApplyFailed",
                                "Failed to apply shared group ownership.",
                                null,
                                ("path", path),
                                ("unixPath", unixPath),
                                ("groupName", groupName),
                                ("gid", gid),
                                ("errno", errno));
                        }

                        uint mode = Convert.ToUInt32(isDirectory ? "2770" : "660", 8);
                        int chmodResult = chmod(unixPath, mode);
                        if (chmodResult != 0)
                        {
                            int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                            TinyIpcLogger.Warning(
                                nameof(UnixSharedStorageHelpers),
                                "PermissionApplyFailed",
                                "Failed to apply group-shared Unix permissions.",
                                null,
                                ("path", path),
                                ("unixPath", unixPath),
                                ("isDirectory", isDirectory),
                                ("mode", isDirectory ? "2770" : "660"),
                                ("errno", errno));
                        }

                        return;
                    }

                    TinyIpcLogger.Warning(
                        nameof(UnixSharedStorageHelpers),
                        "SharedGroupMissing",
                        "Configured shared group could not be resolved; falling back to open shared permissions.",
                        null,
                        ("path", path),
                        ("unixPath", unixPath),
                        ("groupName", groupName));
                }

                uint fallbackMode = Convert.ToUInt32(isDirectory ? "1777" : "666", 8);
                int fallbackChmodResult = chmod(unixPath, fallbackMode);
                if (fallbackChmodResult != 0)
                {
                    int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                    TinyIpcLogger.Warning(
                        nameof(UnixSharedStorageHelpers),
                        "PermissionApplyFailed",
                        "Failed to apply fallback shared Unix permissions.",
                        null,
                        ("path", path),
                        ("unixPath", unixPath),
                        ("isDirectory", isDirectory),
                        ("mode", isDirectory ? "1777" : "666"),
                        ("errno", errno));
                }
            }
            catch (Exception ex)
            {
                TinyIpcLogger.Warning(
                    nameof(UnixSharedStorageHelpers),
                    "PermissionApplyFailed",
                    "Failed to apply Unix permissions to shared path.",
                    ex,
                    ("path", path),
                    ("unixPath", unixPath),
                    ("isDirectory", isDirectory));
            }
        }

        internal static IDisposable AcquireProcessLock(string path)
        {
            RuntimeEnvironmentInfo runtime = RuntimeEnvironmentDetector.Detect();
            return runtime.IsWindowsProcess
                ? new SidecarFileLock(ConvertPathToUnix(path) + ".lck")
                : new NoOpLockHandle();
        }

        private static string ResolveSharedDirectory()
        {
            string? explicitPath = Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string resolved = ResolvePathForCurrentRuntime(explicitPath);

                TinyIpcLogger.Debug(
                    nameof(UnixSharedStorageHelpers),
                    "SharedDirectoryConfigured",
                    "Using configured shared directory.",
                    ("configuredPath", explicitPath),
                    ("resolvedPath", resolved));

                return resolved;
            }

            RuntimeEnvironmentInfo runtime = RuntimeEnvironmentDetector.Detect();

            if (runtime.IsWindowsProcess)
            {
                string fallback = Path.GetTempPath();

                TinyIpcLogger.Warning(
                    nameof(UnixSharedStorageHelpers),
                    "SharedDirectoryDefaulted",
                    "Wine/runtime is using the process temp directory because TINYIPC_SHARED_DIR is not set.",
                    null,
                    ("directory", fallback),
                    ("runtime", runtime.Kind));

                return fallback;
            }

            return "/tmp";
        }

        private static string ResolvePathForCurrentRuntime(string path)
        {
            RuntimeEnvironmentInfo runtime = RuntimeEnvironmentDetector.Detect();

            if (runtime.IsWindowsProcess)
                return NormalizeWindowsPath(path);

            string unixPath = ConvertPathToUnix(path);

            if (!Path.IsPathRooted(unixPath))
                unixPath = Path.GetFullPath(unixPath);

            return unixPath;
        }

        private static string BuildSharedFileName(string name, string kind)
        {
            string? prefix = Environment.GetEnvironmentVariable("TINYIPC_SHARED_PREFIX");
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = "tinyipc";

            string safe = SanitizeName(name);
            string hash = ComputeStableHexHash(name);
            return $"{prefix}_{kind}_{safe}_{hash}.bin";
        }

        private static string SanitizeName(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            string sanitized = sb.ToString().Trim('_');
            if (string.IsNullOrEmpty(sanitized))
                sanitized = "default";

            return sanitized.Length <= 64 ? sanitized : sanitized.Substring(0, 64);
        }

        private static string ComputeStableHexHash(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }

        private static string SanitizeSuffix(string value)
        {
            string sanitized = SanitizeName(value);
            return sanitized.Length <= 16 ? sanitized : sanitized.Substring(0, 16);
        }

        private static string? GetConfiguredGroup()
        {
            string? group = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP");
            return string.IsNullOrWhiteSpace(group) ? null : group;
        }

        private static int GetGroupId(string groupName)
        {
            try
            {
                foreach (string line in File.ReadLines("/etc/group"))
                {
                    if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                        continue;

                    string[] parts = line.Split(':');
                    if (parts.Length >= 3 && string.Equals(parts[0], groupName, StringComparison.Ordinal))
                    {
                        if (int.TryParse(parts[2], out int gid))
                            return gid;
                    }
                }
            }
            catch (Exception ex)
            {
                TinyIpcLogger.Warning(
                    nameof(UnixSharedStorageHelpers),
                    "GroupLookupFailed",
                    "Failed to resolve shared group.",
                    ex,
                    ("groupName", groupName));
            }

            return -1;
        }

        private static string NormalizeWindowsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            try
            {
                string fullPath = Path.GetFullPath(path);
                return fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
            catch
            {
                return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }
        }

        private static string ConvertPathToUnix(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            string normalized = path.Replace('\\', '/');

            if (normalized.Length >= 3 &&
                char.IsLetter(normalized[0]) &&
                normalized[1] == ':' &&
                normalized[2] == '/')
            {
                char drive = char.ToUpperInvariant(normalized[0]);
                string remainder = normalized[3..];

                if (drive == 'Z')
                    return "/" + remainder;

                return $"/mnt/{char.ToLowerInvariant(drive)}/{remainder}";
            }

            return normalized;
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int chown(string path, int owner, int group);

        private sealed class NoOpLockHandle : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class SidecarFileLock : IDisposable
        {
            private readonly FileStream _stream;

            public SidecarFileLock(string lockPath)
            {
                DateTime deadline = DateTime.UtcNow + LockWaitTimeout;

                string? directory = Path.GetDirectoryName(lockPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                while (true)
                {
                    try
                    {
                        _stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        return;
                    }
                    catch (IOException) when (DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(2);
                    }
                }
            }

            public void Dispose()
            {
                _stream.Dispose();
            }
        }
    }
}