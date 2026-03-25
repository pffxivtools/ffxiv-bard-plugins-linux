using System.Reflection;
using System.Security.Cryptography;

namespace XivIpc.Internal;

internal sealed record TinyIpcProcessStamp(
    string AssemblyPath,
    string AssemblyName,
    string InformationalVersion,
    string FileVersion,
    string Sha256,
    string ProcessPath,
    string ProcessSha256)
{
    internal static TinyIpcProcessStamp Create(Type anchorType)
    {
        Assembly assembly = anchorType.Assembly;
        string assemblyPath = ResolveAssemblyPath(assembly);
        AssemblyName assemblyName = assembly.GetName();
        string informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assemblyName.Version?.ToString() ?? string.Empty;
        string fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? string.Empty;
        string sha256 = ComputeSha256(assemblyPath);
        string processPath = Environment.ProcessPath ?? string.Empty;
        string processSha256 = ComputeSha256(processPath);

        return new TinyIpcProcessStamp(
            assemblyPath,
            assemblyName.Name ?? string.Empty,
            informationalVersion,
            fileVersion,
            sha256,
            processPath,
            processSha256);
    }

    private static string ResolveAssemblyPath(Assembly assembly)
    {
        string assemblyName = assembly.GetName().Name ?? string.Empty;
        string baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(assemblyName))
            return string.Empty;

        string[] candidates =
        {
            Path.Combine(baseDirectory, $"{assemblyName}.dll"),
            Path.Combine(baseDirectory, $"{assemblyName}.exe"),
            Path.Combine(baseDirectory, assemblyName)
        };

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    internal static string ComputeSha256(string assemblyPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                return string.Empty;

            using FileStream stream = File.OpenRead(assemblyPath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return string.Empty;
        }
    }
}
