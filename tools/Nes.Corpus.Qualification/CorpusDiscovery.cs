using System.IO.Compression;

namespace Nes.Corpus.Qualification;

internal abstract record RomSource(string ContainerPath)
{
    internal sealed record Direct(string Path) : RomSource(Path);

    internal sealed record ZipEntry(string ArchivePath, int EntryIndex) : RomSource(ArchivePath);
}

internal sealed record RomCandidate(RomSource Source, RomImageHeader Header, long ObservedBytes);

internal sealed record CorpusDiscoveryResult(
    IReadOnlyList<RomCandidate> Candidates,
    IReadOnlyDictionary<SkippedCategory, int> Skipped)
{
    public int Discovered => Candidates.Count + Skipped.Values.Sum();
}

internal static class CorpusDiscovery
{
    private const int HeaderSize = 16;

    public static CorpusDiscoveryResult Discover(string root, int maximumImageBytes)
    {
        var candidates = new List<RomCandidate>();
        var skipped = new Dictionary<SkippedCategory, int>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(path).Equals(".nes", StringComparison.OrdinalIgnoreCase))
            {
                DiscoverDirect(path, maximumImageBytes, candidates, skipped);
            }
            else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                DiscoverArchive(path, maximumImageBytes, candidates, skipped);
            }
        }

        return new CorpusDiscoveryResult(candidates, skipped);
    }

    private static void DiscoverDirect(
        string path,
        int maximumImageBytes,
        ICollection<RomCandidate> candidates,
        IDictionary<SkippedCategory, int> skipped)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            AddCandidate(new RomSource.Direct(path), stream, stream.Length, maximumImageBytes, candidates, skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Increment(skipped, SkippedCategory.InvalidImage);
        }
    }

    private static void DiscoverArchive(
        string path,
        int maximumImageBytes,
        ICollection<RomCandidate> candidates,
        IDictionary<SkippedCategory, int> skipped)
    {
        try
        {
            using var archiveStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                if (!Path.GetExtension(entry.FullName).Equals(".nes", StringComparison.OrdinalIgnoreCase))
                {
                    Increment(skipped, SkippedCategory.NonNesEntry);
                    continue;
                }

                try
                {
                    using var entryStream = entry.Open();
                    AddCandidate(new RomSource.ZipEntry(path, index), entryStream, entry.Length, maximumImageBytes, candidates, skipped);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    Increment(skipped, SkippedCategory.InvalidImage);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Increment(skipped, SkippedCategory.InvalidImage);
        }
    }

    private static void AddCandidate(
        RomSource source,
        Stream stream,
        long availableBytes,
        int maximumImageBytes,
        ICollection<RomCandidate> candidates,
        IDictionary<SkippedCategory, int> skipped)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        var read = 0;
        while (read < header.Length)
        {
            var count = stream.Read(header[read..]);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (RomImageHeaderParser.TryParse(header[..read], availableBytes, maximumImageBytes, out var parsed, out var failure))
        {
            candidates.Add(new RomCandidate(source, parsed!, availableBytes));
        }
        else
        {
            Increment(skipped, failure);
        }
    }

    private static void Increment(IDictionary<SkippedCategory, int> counts, SkippedCategory category)
    {
        counts.TryGetValue(category, out var count);
        counts[category] = count + 1;
    }
}
