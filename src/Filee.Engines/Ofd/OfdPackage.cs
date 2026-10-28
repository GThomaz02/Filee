// The ZIP container of an OFD file, read defensively: parts are looked up by name inside the package and never
// written out under their own names, references ("../Res/a.png", "/Doc_0/Document.xml") are resolved against the
// package root and cannot leave it, and the package is read within DocumentLimits (Documents/DocumentPackage): XML
// without DTDs and with a depth limit, every part with a size limit that is also checked while decompressing, so a
// lying header does not help a zip bomb.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Ofd;

/// <summary>An OFD package opened for reading.</summary>
internal sealed class OfdPackage : IDisposable
{
    /// <summary>Largest picture taken; bigger ones are left out.</summary>
    public const long MaxPictureBytes = 32L * 1024 * 1024;

    private readonly DocumentPackage _package;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public OfdPackage(string path) : this(limits => DocumentPackage.Open(path, limits, NotAPackage), DocumentLimits.Default)
    {
    }

    /// <summary>A package held in memory (the seal picture inside a signature), with its own limit on what it unpacks to.</summary>
    public OfdPackage(byte[] bytes, long maxTotal) : this(
        limits => DocumentPackage.Open(new MemoryStream(bytes, writable: false), limits, NotAPackage),
        DocumentLimits.Default with { MaxPackageBytes = Math.Min(maxTotal, DocumentLimits.Default.MaxPackageBytes) })
    {
    }

    private OfdPackage(Func<DocumentLimits, DocumentPackage> open, DocumentLimits limits)
    {
        _package = open(limits);
        foreach (var entry in _package.Entries)
        {
            // Names that climb out of the package ("../x") are unreachable; of two parts with one name the first wins.
            if (!entry.FullName.EndsWith('/') && Resolve("", entry.FullName) is { } name)
                _entries.TryAdd(name, entry);
        }
    }

    private static InvalidDataException NotAPackage() =>
        new("The file is not an OFD document (it is not a ZIP package). It may be damaged.");

    public void Dispose() => _package.Dispose();

    /// <summary>The limits the package is read with.</summary>
    public DocumentLimits Limits => _package.Limits;

    /// <summary>True when the package has a part of that (resolved) name.</summary>
    public bool Has(string? name) => name is not null && _entries.ContainsKey(name);

    /// <summary>Names of all parts, resolved.</summary>
    public IEnumerable<string> Names => _entries.Keys;

    /// <summary>
    /// A reference inside the package as a part name: relative to <paramref name="baseDir"/>, or to the package root
    /// when it starts with "/". "." and ".." are applied; null when the reference is empty, leaves the package or is
    /// a URL or drive path.
    /// </summary>
    public static string? Resolve(string baseDir, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        reference = reference.Trim().Replace('\\', '/');
        if (reference.Contains(':'))
            return null;
        var parts = new List<string>();
        if (!reference.StartsWith('/'))
            parts.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in reference.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>The folder of a part name ("" at the root).</summary>
    public static string DirName(string name)
    {
        var slash = name.LastIndexOf('/');
        return slash < 0 ? "" : name[..slash];
    }

    /// <summary>An XML part, or null when the package has no such part. Throws for parts that are not XML.</summary>
    public XDocument? LoadXml(string? name)
    {
        if (name is null || !_entries.TryGetValue(name, out var entry))
            return null;
        try
        {
            return _package.LoadXml(entry, preserveWhitespace: true);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The OFD part {name} is not valid XML. The file may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    /// <summary>The bytes of a part; null when it is missing or larger than <paramref name="max"/>.</summary>
    public byte[]? Read(string name, long max) =>
        _entries.TryGetValue(name, out var entry) ? _package.ReadBytes(entry, max) : null;
}
