using System.Collections;
using System.Collections.Concurrent;
using System.Security;

using HaveIBeenPwned.AddressExtractor.Objects;

namespace HaveIBeenPwned.AddressExtractor;

internal sealed class FileCollection : IEnumerable<FileInfo>
{
    private readonly Runtime Runtime;
    private Config Config => Runtime.Config;
    private readonly IDictionary<string, FileInfo> Files;

    public int Count => Files.Count;

    public FileCollection(Runtime runtime, IEnumerable<string> inputs)
    {
        Runtime = runtime;
        Files = CreateSystemSet();
        foreach (var file in GatherFiles(inputs))
        {
            Files[file.FullName] = file;
        }

        Log();
    }

    private IEnumerable<FileInfo> GatherFiles(IEnumerable<string> inputs, bool recursed = false)
    {
        foreach (var file in inputs)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(file);
            }
            catch (Exception ex) when (IsSkippableFileSystemException(ex))
            {
                LogSkippedPath(file, ex);
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (!recursed || Config.OperateRecursively)
                {
                    IEnumerable<string> entries;
                    try
                    {
                        entries = Directory.EnumerateFileSystemEntries(file);
                    }
                    catch (Exception ex) when (IsSkippableFileSystemException(ex))
                    {
                        LogSkippedPath(file, ex);
                        continue;
                    }

                    foreach (var enumerated in GatherFiles(entries, recursed: true))
                    {
                        yield return enumerated;
                    }
                }
            }
            else if (File.Exists(file))
            {
                yield return new FileInfo(file);
            }
        }
    }

    /// <summary>
    /// Gather our <see cref="IEnumerable{String}"/> as a Set so that we don't have duplicates
    /// Windows uses a Case-Insensitive File system, so on it we can mostly ignore casing
    /// </summary>
    private static Dictionary<string, FileInfo> CreateSystemSet()
    {
        var os = Environment.OSVersion;
        if (os.Platform is PlatformID.Win32S or PlatformID.Win32Windows or PlatformID.Win32NT or PlatformID.WinCE)
        {
            return new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        }

        return [];
    }

    private void Log()
    {
        var infos = new ConcurrentDictionary<string, ExtensionInfo>(StringComparer.OrdinalIgnoreCase);
        var count = Files.Count; // Cache the count before removing

        foreach (var file in Files.Values.ToArray())
        {
            var extension = file.Extension is { Length: > 0 } ext ? ext : "(no extension)";
            var info = infos.GetOrAdd(extension, _ => new ExtensionInfo(Runtime, extension.ToLower()));

            try
            {
                info.AddFile(file);
            }
            catch (Exception ex) when (IsSkippableFileSystemException(ex))
            {
                Files.Remove(file.FullName);
                LogSkippedPath(file.FullName, ex);
                continue;
            }

            // Remove ignored files
            if (!Config.ProcessAllExtensions && !info.Parsing.Read)
            {
                Files.Remove(file.FullName);
            }
        }

        var sorted = infos.Values
            .OrderBy(info => info.Parsing.Read ? -info.Count : 0);
        Output.Write($"Found {count:n0} files:");
        foreach (var info in sorted)
        {
            Output.Write($"  {info.Extension,-6} {info.Count:n0} files: {ByteExtensions.Format(info.Bytes)}{(info.Parsing.Read ? string.Empty : $", Skipping ({info.Parsing.Error})")}");
        }

        var output = Config.OutputFilePath;
        var report = Config.ReportFilePath;
        Output.Write($"Output will {(string.IsNullOrWhiteSpace(output) ? "not be saved" : $"be saved to \"{output}\"")}.");
        Output.Write($"Report will {(string.IsNullOrWhiteSpace(report) ? "not be saved" : $"be saved to \"{report}\"")}.");
        Output.Write($"Quick '@' scan will be used for files {ByteExtensions.Format(Config.MinimumFileSizeForAtSymbolQuickScan)} and larger.");
    }

    /// <inheritdoc />
    public IEnumerator<FileInfo> GetEnumerator()
        => Files.Values.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    private static bool IsSkippableFileSystemException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException or ArgumentException;

    private void LogSkippedPath(string path, Exception ex)
    {
        if (Config.Debug)
        {
            Output.Exception(new IOException($"Skipping '{path}':", ex));
            return;
        }

        Output.Notice($"Skipping '{path}': {ex.Message}");
    }

    private class ExtensionInfo(Runtime runtime, string extension)
    {
        public readonly string Extension = extension;
        public readonly FileExtensionParsing Parsing = runtime.GetExtension(extension);

        public int Count { get; private set; }
        public long Bytes { get; private set; }

        public void AddFile(FileInfo info)
        {
            Count++;
            Bytes += info.Length;
        }
    }
}
