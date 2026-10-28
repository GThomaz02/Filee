// A mutation fuzzer for document packages (HWPX, DOCX): it takes a valid package written in code, changes it a little
// at random and hands it to a reader. Most changes edit the XML tree, so the file stays well-formed and reaches the
// reader's logic (attributes set to extreme or foreign values, elements removed, repeated, renamed or moved inside one
// another); some edit the text or the bytes, so the XML and ZIP error paths are covered too. A reader may refuse a
// file only with InvalidDataException or ConversionRefusedException; anything else, or a read that takes too long, is
// a failure, and the input that caused it is kept for the report.

using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;

namespace Filee.Engines.Tests;

internal sealed class DocumentFuzzer
{
    /// <summary>Attribute values that have broken readers before: bounds of every integer type, non-numbers, paths.</summary>
    private static readonly string[] Interesting =
    [
        "", "0", "1", "-1", "2", "255", "65535", "65536", "-32768", "2147483647", "-2147483648", "2147483648", "4294967295",
        "4294967296", "9223372036854775807", "-9223372036854775808", "18446744073709551615", "99999999999999999999999",
        "1e308", "-1e308", "1e-308", "NaN", "Infinity", "-Infinity", "0.5", "-0.0", "1.#INF", "0x7FFFFFFF", "#", "#FFF",
        "#GGGGGG", "#AARRGGBB", "#FF00FF00", "none", "auto", "true", "false", "x", " ", "\t", "%", "50%", "1in", "-5cm",
        "12pt", "999999999in", "../../../../../etc/passwd", "/word/document.xml", "Contents/header.xml", "BinData/image1.png",
        "C:\\Windows\\win.ini", "\\\\server\\share\\x", "file:///c:/x", "http://example.invalid/a", "rId1", "rId10", "0,0",
        "1,2,3", "-1,-1", ",,,", "a:b", "image.png:stream", "con", "nul.png", new string('9', 400), new string('a', 5000),
    ];

    /// <summary>Numbers at the edges of what readers parse and compute with.</summary>
    private static readonly string[] Numbers =
    [
        "0", "-1", "1", "2", "-2147483648", "2147483647", "2147483648", "4294967295", "4294967296", "-4294967296",
        "9223372036854775807", "-9223372036854775808", "1e308", "-1e308", "1e30", "-1e30", "100000000", "-100000000",
        "65536", "1073741824", "-1073741824", "0.0001", "NaN", "Infinity",
    ];

    private readonly Random _random;
    private readonly List<(string Name, byte[] Bytes)> _seed;
    private readonly List<string> _xmlParts;

    public DocumentFuzzer(byte[] seedPackage, Random random)
    {
        _random = random;
        _seed = [];
        using (var zip = new ZipArchive(new MemoryStream(seedPackage), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                _seed.Add((entry.FullName, copy.ToArray()));
            }
        }
        _xmlParts = _seed.Where(p => IsXml(p.Name)).Select(p => p.Name).ToList();
    }

    private static bool IsXml(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".hpf", StringComparison.OrdinalIgnoreCase);

    /// <summary>A mutated copy of the seed package.</summary>
    public byte[] Next()
    {
        var parts = _seed.Select(p => (p.Name, p.Bytes)).ToList();
        var rounds = 1 + _random.Next(4);
        for (var i = 0; i < rounds; i++)
        {
            var choice = _random.Next(100);
            if (choice < 70 && _xmlParts.Count > 0)
                MutateTree(parts);
            else if (choice < 82 && _xmlParts.Count > 0)
                MutateText(parts);
            else if (choice < 90)
                MutateBinary(parts);
            else if (choice < 95 && parts.Count > 1)
                parts.RemoveAt(_random.Next(parts.Count));
            else
                Splice(parts);
        }
        var package = Pack(parts);
        if (_random.Next(100) < 4)
        {
            // Bytes of the ZIP itself: headers, sizes, compressed data.
            for (var flips = 1 + _random.Next(8); flips > 0; flips--)
                package[_random.Next(package.Length)] = (byte)_random.Next(256);
        }
        return package;
    }

    private byte[] Pack(List<(string Name, byte[] Bytes)> parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in parts)
            {
                using var stream = zip.CreateEntry(name, _random.Next(4) == 0 ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    /// <summary>An XML part, the larger ones (the body, the styles) more often.</summary>
    private int PickXml(List<(string Name, byte[] Bytes)> parts)
    {
        var candidates = Enumerable.Range(0, parts.Count).Where(i => _xmlParts.Contains(parts[i].Name)).ToList();
        if (candidates.Count == 0)
            return -1;
        if (_random.Next(4) == 0)
            return candidates[_random.Next(candidates.Count)];
        var total = candidates.Sum(i => (long)parts[i].Bytes.Length + 1);
        var pick = _random.NextInt64(total);
        foreach (var i in candidates)
        {
            pick -= parts[i].Bytes.Length + 1;
            if (pick < 0)
                return i;
        }
        return candidates[^1];
    }

    // ───────────────────────── Tree ─────────────────────────

    private void MutateTree(List<(string Name, byte[] Bytes)> parts)
    {
        var index = PickXml(parts);
        if (index < 0)
            return;
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(parts[index].Bytes), LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return; // an earlier round broke it
        }
        var elements = document.Descendants().ToList();
        if (elements.Count == 0)
            return;
        for (var edits = 1 + _random.Next(3); edits > 0; edits--)
        {
            var element = elements[_random.Next(elements.Count)];
            switch (_random.Next(9))
            {
                case 0 or 1 or 2:
                    SetAttribute(element, elements);
                    break;
                case 3:
                    if (element.Parent is not null)
                        element.Remove();
                    break;
                case 4:
                    // The same element many times: counts, ids and caches.
                    if (element.Parent is not null)
                    {
                        var copies = _random.Next(3) == 0 ? 1 + _random.Next(200) : 1 + _random.Next(3);
                        for (var c = 0; c < copies; c++)
                            element.AddAfterSelf(new XElement(element));
                    }
                    break;
                case 5:
                    {
                        // An element inside another: tables in cells in tables, notes in notes, groups in groups.
                        var host = elements[_random.Next(elements.Count)];
                        if (!host.AncestorsAndSelf().Contains(element) && !element.AncestorsAndSelf().Contains(host))
                            host.Add(new XElement(element));
                        else if (host == element)
                            host.Add(new XElement(element));
                        break;
                    }
                case 6:
                    {
                        // The name of another element of the part.
                        var other = elements[_random.Next(elements.Count)];
                        element.Name = other.Name;
                        break;
                    }
                case 7:
                    element.ReplaceNodes(_random.Next(3) switch
                    {
                        0 => "",
                        1 => new string('가', 1 + _random.Next(2000)),
                        _ => Interesting[_random.Next(Interesting.Length)],
                    });
                    break;
                default:
                    {
                        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
                        if (attributes.Count > 0)
                            attributes[_random.Next(attributes.Count)].Remove();
                        break;
                    }
            }
            elements = document.Descendants().ToList();
            if (elements.Count == 0)
                break;
        }
        var text = new StringBuilder();
        try
        {
            using var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = false, CheckCharacters = false });
            document.Save(writer);
        }
        catch (Exception ex) when (ex is ArgumentException or XmlException or InvalidOperationException)
        {
            return; // a tree XML can't hold (xml:space="", a prefix bound twice): this edit is dropped
        }
        parts[index] = (parts[index].Name, Encoding.UTF8.GetBytes(text.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal)));
    }

    private void SetAttribute(XElement element, List<XElement> elements)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        if (_random.Next(2) == 0)
        {
            // Any attribute of the part, so the few that matter (a table's column count) come up as often as any.
            attributes = [.. elements.SelectMany(e => e.Attributes()).Where(a => !a.IsNamespaceDeclaration)];
        }
        var target = attributes.Count > 0 ? attributes[_random.Next(attributes.Count)] : null;
        string value;
        if (_random.Next(3) == 0)
        {
            // A value from elsewhere in the part: ids that point at the wrong thing.
            var source = elements[_random.Next(elements.Count)].Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
            value = source.Count > 0 ? source[_random.Next(source.Count)].Value : Interesting[_random.Next(Interesting.Length)];
        }
        else if (target is not null && double.TryParse(target.Value, System.Globalization.CultureInfo.InvariantCulture, out _) && _random.Next(4) > 0)
        {
            // A number stays a number, of an extreme size.
            value = Numbers[_random.Next(Numbers.Length)];
        }
        else
        {
            value = Interesting[_random.Next(Interesting.Length)];
        }
        if (target is not null && _random.Next(5) > 0)
        {
            target.Value = value;
        }
        else
        {
            // An attribute this element does not have, named like one elsewhere.
            var named = elements[_random.Next(elements.Count)].Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
            if (named.Count > 0)
                element.SetAttributeValue(named[_random.Next(named.Count)].Name, value);
        }
    }

    // ───────────────────────── Text and bytes ─────────────────────────

    private void MutateText(List<(string Name, byte[] Bytes)> parts)
    {
        var index = PickXml(parts);
        if (index < 0)
            return;
        var bytes = parts[index].Bytes.ToList();
        if (bytes.Count == 0)
            return;
        switch (_random.Next(5))
        {
            case 0:
                for (var flips = 1 + _random.Next(4); flips > 0; flips--)
                    bytes[_random.Next(bytes.Count)] = (byte)_random.Next(256);
                break;
            case 1:
                {
                    var start = _random.Next(bytes.Count);
                    bytes.RemoveRange(start, Math.Min(bytes.Count - start, 1 + _random.Next(64)));
                    break;
                }
            case 2:
                {
                    var start = _random.Next(bytes.Count);
                    var span = bytes.GetRange(start, Math.Min(bytes.Count - start, 1 + _random.Next(256)));
                    bytes.InsertRange(_random.Next(bytes.Count), span);
                    break;
                }
            case 3:
                bytes = bytes.Take(_random.Next(bytes.Count)).ToList(); // cut short
                break;
            default:
                {
                    // A DTD with entities in front of the root: must be refused, never expanded or resolved.
                    var text = Encoding.UTF8.GetString([.. bytes]);
                    var root = text.IndexOf('<', Math.Max(0, text.IndexOf("?>", StringComparison.Ordinal)));
                    if (root < 0)
                        root = 0;
                    var dtd = _random.Next(2) == 0
                        ? "<!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>"
                        : "<!DOCTYPE x SYSTEM \"file:///c:/windows/win.ini\">";
                    bytes = [.. Encoding.UTF8.GetBytes(text.Insert(root, dtd))];
                    break;
                }
        }
        parts[index] = (parts[index].Name, [.. bytes]);
    }

    private void MutateBinary(List<(string Name, byte[] Bytes)> parts)
    {
        var binaries = Enumerable.Range(0, parts.Count).Where(i => !IsXml(parts[i].Name)).ToList();
        if (binaries.Count == 0)
            return;
        var index = binaries[_random.Next(binaries.Count)];
        var bytes = (byte[])parts[index].Bytes.Clone();
        if (bytes.Length >= 24 && bytes[0] == 0x89 && _random.Next(2) == 0)
        {
            // A PNG that claims an enormous size.
            bytes[16] = bytes[20] = 0x7F;
            bytes[17] = bytes[21] = 0xFF;
        }
        else if (bytes.Length > 0)
        {
            for (var flips = 1 + _random.Next(8); flips > 0; flips--)
                bytes[_random.Next(bytes.Length)] = (byte)_random.Next(256);
            if (_random.Next(4) == 0)
                bytes = bytes[.._random.Next(bytes.Length)];
        }
        parts[index] = (parts[index].Name, bytes);
    }

    /// <summary>A part under the name of another, or an XML part where a picture should be.</summary>
    private void Splice(List<(string Name, byte[] Bytes)> parts)
    {
        if (parts.Count < 2)
            return;
        var from = parts[_random.Next(parts.Count)];
        var to = _random.Next(parts.Count);
        parts[to] = (parts[to].Name, from.Bytes);
    }

    // ───────────────────────── Running ─────────────────────────

    /// <summary>What a reader did with one input.</summary>
    public enum Outcome
    {
        Read,
        Refused,
    }

    /// <summary>
    /// Runs <paramref name="read"/>; returns how it ended, or throws
    /// <see cref="FuzzFailure"/> for an exception a reader must not throw or a read longer than <paramref name="timeout"/>.
    /// </summary>
    public static Outcome Run(Action read, TimeSpan timeout)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                read();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, 1024 * 1024); // the smallest stack a conversion runs on: what the limits allow must fit in it
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(timeout))
            throw new FuzzFailure($"The read took longer than {timeout.TotalSeconds:0} s.", null);
        return error switch
        {
            null => Outcome.Read,
            InvalidDataException or ConversionRefusedException => Outcome.Refused,
            _ => throw new FuzzFailure($"{error.GetType().Name}: {error.Message}", error),
        };
    }

    public sealed class FuzzFailure(string message, Exception? inner) : Exception(message, inner);
}
