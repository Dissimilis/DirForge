using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace DirForge.Services;

public sealed class ArchiveBrowseService
{
    private static readonly StringComparer PathComparer = StringComparer.Ordinal;
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public bool IsSupportedArchiveName(string fileName)
    {
        return ResolveArchiveKind(fileName) != ArchiveKind.Unknown;
    }

    public bool TryNormalizeVirtualPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var raw = value.Replace('\\', '/').Trim().Trim('/');
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return TryValidateAndJoinSegments(segments, out normalized);
    }

    public ArchiveBrowseListing BuildListing(string archiveFilePath, string currentPath, int operationTimeBudgetMs = 0)
    {
        var rawEntries = ReadRawEntries(archiveFilePath, operationTimeBudgetMs);
        var nodes = BuildNodeMap(rawEntries);

        if (!string.IsNullOrEmpty(currentPath) && !nodes.Keys.Any(path => IsPathWithin(path, currentPath)))
        {
            return new ArchiveBrowseListing(currentPath, GetParentPath(currentPath), []);
        }

        var children = BuildChildren(nodes, currentPath);
        return new ArchiveBrowseListing(currentPath, GetParentPath(currentPath), children);
    }

    public bool TryGetEntryDownloadInfo(string archiveFilePath, string entryPath, out ArchiveEntryDownloadInfo? info)
    {
        info = default;
        if (!TryNormalizeVirtualPath(entryPath, out var normalizedPath) || string.IsNullOrEmpty(normalizedPath))
        {
            return false;
        }

        var kind = ResolveArchiveKind(Path.GetFileName(archiveFilePath));
        return kind switch
        {
            ArchiveKind.Zip => TryGetZipEntryDownloadInfo(archiveFilePath, normalizedPath, out info),
            ArchiveKind.Tar => TryGetTarEntryDownloadInfo(archiveFilePath, normalizedPath, compressed: false, out info),
            ArchiveKind.TarGz => TryGetTarEntryDownloadInfo(archiveFilePath, normalizedPath, compressed: true, out info),
            ArchiveKind.GZip => TryGetGzipEntryDownloadInfo(archiveFilePath, normalizedPath, out info),
            _ => false
        };
    }

    public async Task<long> CopyEntryToAsync(string archiveFilePath, string entryPath, Stream destination, CancellationToken cancellationToken)
    {
        if (!TryNormalizeVirtualPath(entryPath, out var normalizedPath) || string.IsNullOrEmpty(normalizedPath))
        {
            throw new FileNotFoundException("Archive entry path is invalid.");
        }

        var kind = ResolveArchiveKind(Path.GetFileName(archiveFilePath));
        return kind switch
        {
            ArchiveKind.Zip => await CopyZipEntryToAsync(archiveFilePath, normalizedPath, destination, cancellationToken),
            ArchiveKind.Tar => await CopyTarEntryToAsync(archiveFilePath, normalizedPath, compressed: false, destination, cancellationToken),
            ArchiveKind.TarGz => await CopyTarEntryToAsync(archiveFilePath, normalizedPath, compressed: true, destination, cancellationToken),
            ArchiveKind.GZip => await CopyGzipEntryToAsync(archiveFilePath, normalizedPath, destination, cancellationToken),
            _ => throw new NotSupportedException("Archive type is not supported.")
        };
    }

    public async Task<byte[]> ReadEntryHeadBytesAsync(
        string archiveFilePath,
        string entryPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            return [];
        }

        if (!TryNormalizeVirtualPath(entryPath, out var normalizedPath) || string.IsNullOrEmpty(normalizedPath))
        {
            throw new FileNotFoundException("Archive entry path is invalid.");
        }

        var kind = ResolveArchiveKind(Path.GetFileName(archiveFilePath));
        return kind switch
        {
            ArchiveKind.Zip => await ReadZipEntryHeadBytesAsync(archiveFilePath, normalizedPath, maxBytes, cancellationToken),
            ArchiveKind.Tar => await ReadTarEntryHeadBytesAsync(archiveFilePath, normalizedPath, compressed: false, maxBytes, cancellationToken),
            ArchiveKind.TarGz => await ReadTarEntryHeadBytesAsync(archiveFilePath, normalizedPath, compressed: true, maxBytes, cancellationToken),
            ArchiveKind.GZip => await ReadGzipEntryHeadBytesAsync(archiveFilePath, normalizedPath, maxBytes, cancellationToken),
            _ => throw new NotSupportedException("Archive type is not supported.")
        };
    }

    public static string GetEntryFileName(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
        {
            return "download";
        }

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    private static IReadOnlyList<ArchiveBrowseEntry> BuildChildren(
        Dictionary<string, ArchiveNode> nodes,
        string currentPath)
    {
        var childrenByPath = new Dictionary<string, ArchiveBrowseEntry>(PathComparer);
        var prefix = string.IsNullOrEmpty(currentPath) ? string.Empty : currentPath + "/";

        foreach (var node in nodes.Values)
        {
            string remainder;
            if (string.IsNullOrEmpty(prefix))
            {
                remainder = node.Path;
            }
            else if (node.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                remainder = node.Path[prefix.Length..];
            }
            else
            {
                continue;
            }

            if (string.IsNullOrEmpty(remainder))
            {
                continue;
            }

            var slashIndex = remainder.IndexOf('/');
            if (slashIndex >= 0)
            {
                var childName = remainder[..slashIndex];
                var childPath = string.IsNullOrEmpty(currentPath) ? childName : currentPath + "/" + childName;
                if (!childrenByPath.ContainsKey(childPath))
                {
                    childrenByPath[childPath] = new ArchiveBrowseEntry(
                        Name: childName,
                        Path: childPath,
                        IsDirectory: true,
                        Size: 0,
                        ModifiedUtc: null);
                }
                continue;
            }

            childrenByPath[node.Path] = new ArchiveBrowseEntry(
                Name: remainder,
                Path: node.Path,
                IsDirectory: node.IsDirectory,
                Size: node.IsDirectory ? 0 : node.Size,
                ModifiedUtc: node.ModifiedUtc);
        }

        return childrenByPath.Values
            .OrderBy(static entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(static entry => entry.Name, NameComparer)
            .ToArray();
    }

    private static Dictionary<string, ArchiveNode> BuildNodeMap(IReadOnlyCollection<ArchiveRawEntry> rawEntries)
    {
        var nodes = new Dictionary<string, ArchiveNode>(PathComparer);

        foreach (var entry in rawEntries)
        {
            AddParentDirectories(nodes, entry.Path);

            if (entry.IsDirectory)
            {
                nodes[entry.Path] = new ArchiveNode(entry.Path, IsDirectory: true, Size: 0, ModifiedUtc: null);
                continue;
            }

            nodes[entry.Path] = new ArchiveNode(entry.Path, IsDirectory: false, entry.Size, entry.ModifiedUtc);
        }

        return nodes;
    }

    private static void AddParentDirectories(Dictionary<string, ArchiveNode> nodes, string entryPath)
    {
        var segments = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return;
        }

        var parent = segments[0];
        for (var i = 1; i < segments.Length; i++)
        {
            if (!nodes.ContainsKey(parent))
            {
                nodes[parent] = new ArchiveNode(parent, IsDirectory: true, Size: 0, ModifiedUtc: null);
            }

            parent += "/" + segments[i];
        }
    }

    private static bool IsPathWithin(string path, string scope)
    {
        return path.Equals(scope, StringComparison.Ordinal) || path.StartsWith(scope + "/", StringComparison.Ordinal);
    }

    private static string GetParentPath(string currentPath)
    {
        if (string.IsNullOrEmpty(currentPath))
        {
            return string.Empty;
        }

        var slashIndex = currentPath.LastIndexOf('/');
        return slashIndex <= 0 ? string.Empty : currentPath[..slashIndex];
    }

    private static IReadOnlyList<ArchiveRawEntry> ReadRawEntries(string archiveFilePath, int operationTimeBudgetMs = 0)
    {
        Stopwatch? timer = operationTimeBudgetMs > 0 ? Stopwatch.StartNew() : null;
        var kind = ResolveArchiveKind(Path.GetFileName(archiveFilePath));
        return kind switch
        {
            ArchiveKind.Zip => ReadZipEntries(archiveFilePath, timer, operationTimeBudgetMs),
            ArchiveKind.Tar => ReadTarEntries(archiveFilePath, compressed: false, timer, operationTimeBudgetMs),
            ArchiveKind.TarGz => ReadTarEntries(archiveFilePath, compressed: true, timer, operationTimeBudgetMs),
            ArchiveKind.GZip => ReadGzipEntry(archiveFilePath),
            _ => []
        };
    }

    private static List<ArchiveRawEntry> ReadZipEntries(
        string archiveFilePath, Stopwatch? timeBudget = null, int timeBudgetMs = 0)
    {
        var entries = new List<ArchiveRawEntry>();
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var zipEntry in archive.Entries)
        {
            if (timeBudget is not null && timeBudget.ElapsedMilliseconds >= timeBudgetMs)
            {
                break;
            }

            if (!TryNormalizeEntryName(zipEntry.FullName, out var normalizedPath, out var isDirectoryFromPath))
            {
                continue;
            }

            var isDirectory = isDirectoryFromPath || string.IsNullOrEmpty(zipEntry.Name);
            entries.Add(new ArchiveRawEntry(
                normalizedPath,
                isDirectory,
                isDirectory ? 0 : zipEntry.Length,
                isDirectory ? null : zipEntry.LastWriteTime.UtcDateTime));
        }

        return entries;
    }

    private static List<ArchiveRawEntry> ReadTarEntries(
        string archiveFilePath, bool compressed, Stopwatch? timeBudget = null, int timeBudgetMs = 0)
    {
        var entries = new List<ArchiveRawEntry>();
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream source = fs;
        if (compressed)
        {
            source = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
        }

        using (source)
        using (var tarReader = new TarReader(source, leaveOpen: false))
        {
            TarEntry? tarEntry;
            while ((tarEntry = tarReader.GetNextEntry()) is not null)
            {
                if (timeBudget is not null && timeBudget.ElapsedMilliseconds >= timeBudgetMs)
                {
                    break;
                }

                if (!TryNormalizeEntryName(tarEntry.Name, out var normalizedPath, out var isDirectoryFromPath))
                {
                    continue;
                }

                var isDirectory = isDirectoryFromPath || tarEntry.EntryType == TarEntryType.Directory;
                entries.Add(new ArchiveRawEntry(
                    normalizedPath,
                    isDirectory,
                    isDirectory ? 0 : tarEntry.Length,
                    tarEntry.ModificationTime.UtcDateTime));
            }
        }

        return entries;
    }

    private static List<ArchiveRawEntry> ReadGzipEntry(string archiveFilePath)
    {
        var pseudoEntryPath = GetGzipPseudoEntryPath(archiveFilePath);
        return
        [
            new ArchiveRawEntry(
                pseudoEntryPath,
                IsDirectory: false,
                Size: 0,
                ModifiedUtc: File.GetLastWriteTimeUtc(archiveFilePath))
        ];
    }

    private static string GetGzipPseudoEntryPath(string archiveFilePath)
    {
        var archiveName = Path.GetFileName(archiveFilePath);
        var candidateName = archiveName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? archiveName[..^3]
            : archiveName + ".out";
        if (string.IsNullOrWhiteSpace(candidateName))
        {
            candidateName = "file";
        }

        return candidateName.Replace('\\', '/').Trim().Trim('/');
    }

    private static bool TryGetZipEntryDownloadInfo(string archiveFilePath, string entryPath, out ArchiveEntryDownloadInfo? info)
    {
        info = default;
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        var zipEntry = FindZipEntry(archive, entryPath);
        if (zipEntry is null)
        {
            return false;
        }

        info = new ArchiveEntryDownloadInfo(
            entryPath,
            GetEntryFileName(entryPath),
            zipEntry.Length,
            zipEntry.LastWriteTime.UtcDateTime);
        return true;
    }

    private static bool TryGetTarEntryDownloadInfo(string archiveFilePath, string entryPath, bool compressed, out ArchiveEntryDownloadInfo? info)
    {
        info = default;
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream source = compressed
            ? new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false)
            : fs;

        using (source)
        using (var tarReader = new TarReader(source, leaveOpen: false))
        {
            var tarEntry = FindTarEntry(tarReader, entryPath);
            if (tarEntry is null)
            {
                return false;
            }

            info = new ArchiveEntryDownloadInfo(
                entryPath,
                GetEntryFileName(entryPath),
                tarEntry.Length,
                tarEntry.ModificationTime.UtcDateTime);
            return true;
        }
    }

    private static bool TryGetGzipEntryDownloadInfo(string archiveFilePath, string entryPath, out ArchiveEntryDownloadInfo? info)
    {
        info = default;
        var pseudoPath = GetGzipPseudoEntryPath(archiveFilePath);
        if (!pseudoPath.Equals(entryPath, StringComparison.Ordinal))
        {
            return false;
        }

        info = new ArchiveEntryDownloadInfo(
            pseudoPath,
            GetEntryFileName(pseudoPath),
            Size: null,
            File.GetLastWriteTimeUtc(archiveFilePath));
        return true;
    }

    private static async Task<long> CopyZipEntryToAsync(string archiveFilePath, string entryPath, Stream destination, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        var zipEntry = FindZipEntry(archive, entryPath)
            ?? throw new FileNotFoundException("Archive entry not found.", entryPath);
        await using var entryStream = zipEntry.Open();
        return await CopyToAsync(entryStream, destination, cancellationToken);
    }

    private static async Task<long> CopyTarEntryToAsync(
        string archiveFilePath,
        string entryPath,
        bool compressed,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream source = compressed
            ? new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false)
            : fs;

        using (source)
        using (var tarReader = new TarReader(source, leaveOpen: false))
        {
            var tarEntry = FindTarEntry(tarReader, entryPath)
                ?? throw new FileNotFoundException("Archive entry not found.", entryPath);
            if (tarEntry.DataStream is null)
            {
                throw new FileNotFoundException("Archive entry not found.", entryPath);
            }

            await using var entryStream = tarEntry.DataStream;
            return await CopyToAsync(entryStream, destination, cancellationToken);
        }
    }

    private static async Task<long> CopyGzipEntryToAsync(string archiveFilePath, string entryPath, Stream destination, CancellationToken cancellationToken)
    {
        var pseudoPath = GetGzipPseudoEntryPath(archiveFilePath);
        if (!pseudoPath.Equals(entryPath, StringComparison.Ordinal))
        {
            throw new FileNotFoundException("Archive entry not found.", entryPath);
        }

        await using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
        return await CopyToAsync(gzip, destination, cancellationToken);
    }

    private static async Task<byte[]> ReadZipEntryHeadBytesAsync(
        string archiveFilePath,
        string entryPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        var zipEntry = FindZipEntry(archive, entryPath)
            ?? throw new FileNotFoundException("Archive entry not found.", entryPath);
        await using var entryStream = zipEntry.Open();
        return await ReadStreamHeadBytesAsync(entryStream, maxBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadTarEntryHeadBytesAsync(
        string archiveFilePath,
        string entryPath,
        bool compressed,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream source = compressed
            ? new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false)
            : fs;

        using (source)
        using (var tarReader = new TarReader(source, leaveOpen: false))
        {
            var tarEntry = FindTarEntry(tarReader, entryPath)
                ?? throw new FileNotFoundException("Archive entry not found.", entryPath);
            if (tarEntry.DataStream is null)
            {
                throw new FileNotFoundException("Archive entry not found.", entryPath);
            }

            await using var entryStream = tarEntry.DataStream;
            return await ReadStreamHeadBytesAsync(entryStream, maxBytes, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadGzipEntryHeadBytesAsync(
        string archiveFilePath,
        string entryPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var pseudoPath = GetGzipPseudoEntryPath(archiveFilePath);
        if (!pseudoPath.Equals(entryPath, StringComparison.Ordinal))
        {
            throw new FileNotFoundException("Archive entry not found.", entryPath);
        }

        await using var fs = new FileStream(archiveFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
        return await ReadStreamHeadBytesAsync(gzip, maxBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadStreamHeadBytesAsync(Stream source, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            return [];
        }

        var chunkSize = Math.Min(maxBytes, 1024 * 1024);
        var buffer = new byte[chunkSize];
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        while (output.Length < maxBytes)
        {
            var remaining = maxBytes - (int)output.Length;
            var readCount = Math.Min(buffer.Length, remaining);
            var bytesRead = await source.ReadAsync(buffer.AsMemory(0, readCount), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            output.Write(buffer, 0, bytesRead);
        }

        return output.ToArray();
    }

    private static async Task<long> CopyToAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        long total = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            total += bytesRead;
        }

        return total;
    }

    private static ZipArchiveEntry? FindZipEntry(ZipArchive archive, string entryPath)
    {
        foreach (var zipEntry in archive.Entries)
        {
            if (!TryNormalizeEntryName(zipEntry.FullName, out var normalizedPath, out var isDirectoryFromPath))
            {
                continue;
            }

            if (isDirectoryFromPath || string.IsNullOrEmpty(zipEntry.Name))
            {
                continue;
            }

            if (normalizedPath.Equals(entryPath, StringComparison.Ordinal))
            {
                return zipEntry;
            }
        }

        return null;
    }

    private static TarEntry? FindTarEntry(TarReader tarReader, string entryPath)
    {
        TarEntry? tarEntry;
        while ((tarEntry = tarReader.GetNextEntry()) is not null)
        {
            if (!TryNormalizeEntryName(tarEntry.Name, out var normalizedPath, out var isDirectoryFromPath))
            {
                continue;
            }

            if (isDirectoryFromPath || tarEntry.EntryType == TarEntryType.Directory)
            {
                continue;
            }

            if (normalizedPath.Equals(entryPath, StringComparison.Ordinal))
            {
                return tarEntry;
            }
        }

        return null;
    }

    private static bool TryNormalizeEntryName(string? rawName, out string normalizedPath, out bool isDirectoryFromPath)
    {
        normalizedPath = string.Empty;
        isDirectoryFromPath = false;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        var normalized = rawName.Replace('\\', '/').Trim();
        isDirectoryFromPath = normalized.EndsWith("/", StringComparison.Ordinal);
        normalized = normalized.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!TryValidateAndJoinSegments(segments, out normalizedPath))
        {
            return false;
        }

        return !string.IsNullOrEmpty(normalizedPath);
    }

    private static bool TryValidateAndJoinSegments(string[] segments, out string result)
    {
        result = string.Empty;
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOf('\0') >= 0)
            {
                return false;
            }

            normalized.Add(segment);
        }

        result = string.Join('/', normalized);
        return true;
    }

    private static ArchiveKind ResolveArchiveKind(string fileName)
    {
        var lowerName = fileName.ToLowerInvariant();
        if (lowerName.EndsWith(".tar.gz", StringComparison.Ordinal) || lowerName.EndsWith(".tgz", StringComparison.Ordinal))
        {
            return ArchiveKind.TarGz;
        }

        if (lowerName.EndsWith(".zip", StringComparison.Ordinal))
        {
            return ArchiveKind.Zip;
        }

        if (lowerName.EndsWith(".tar", StringComparison.Ordinal))
        {
            return ArchiveKind.Tar;
        }

        if (lowerName.EndsWith(".gz", StringComparison.Ordinal))
        {
            return ArchiveKind.GZip;
        }

        return ArchiveKind.Unknown;
    }

    private enum ArchiveKind
    {
        Unknown = 0,
        Zip = 1,
        Tar = 2,
        TarGz = 3,
        GZip = 4
    }

    private sealed record ArchiveRawEntry(
        string Path,
        bool IsDirectory,
        long Size,
        DateTime? ModifiedUtc);

    private sealed record ArchiveNode(
        string Path,
        bool IsDirectory,
        long Size,
        DateTime? ModifiedUtc);
}

public sealed record ArchiveBrowseListing(
    string CurrentPath,
    string ParentPath,
    IReadOnlyList<ArchiveBrowseEntry> Entries);

public sealed record ArchiveBrowseEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTime? ModifiedUtc);

public sealed record ArchiveEntryDownloadInfo(
    string Path,
    string FileName,
    long? Size,
    DateTime? ModifiedUtc);
