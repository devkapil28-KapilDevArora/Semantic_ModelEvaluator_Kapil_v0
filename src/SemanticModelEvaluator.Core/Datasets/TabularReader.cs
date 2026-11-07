using System.Text;
using System.Text.Json;

namespace SemanticModelEvaluator.Core.Datasets;

public enum TableFormat { Csv, Tsv, Json, JsonLines }

/// <summary>Simple in-memory table used for import previews and mapping.</summary>
public sealed class TabularData
{
    public required string SourcePath { get; init; }
    public required TableFormat Format { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<string?[]> Rows { get; init; }

    public int IndexOf(string column) => Columns.ToList().FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string?> Values(string column)
    {
        int i = IndexOf(column);
        if (i < 0) yield break;
        foreach (var r in Rows) yield return i < r.Length ? r[i] : null;
    }
}

public static class TabularReader
{
    public const long MaxFileBytes = 1L << 30;

    public static TableFormat DetectFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => TableFormat.Csv,
        ".tsv" or ".tab" => TableFormat.Tsv,
        ".json" => TableFormat.Json,
        ".jsonl" or ".ndjson" => TableFormat.JsonLines,
        var ext => throw new NotSupportedException($"Unsupported table format '{ext}'. Supported: .csv, .tsv, .json, .jsonl.")
    };

    public static TabularData Read(string path, TableFormat? format = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Dataset table not found.", path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("Dataset table exceeds the 1 GB import limit.");
        var fmt = format ?? DetectFormat(path);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return fmt switch
        {
            TableFormat.Csv => ParseDelimited(reader, ',', path, fmt),
            TableFormat.Tsv => ParseDelimited(reader, '\t', path, fmt),
            TableFormat.Json => ParseJson(reader.ReadToEnd(), path),
            TableFormat.JsonLines => ParseJsonLines(reader, path),
            _ => throw new NotSupportedException()
        };
    }

    public static TabularData ParseCsvText(string text, string path = "inline.csv") =>
        ParseDelimited(new StringReader(text), ',', path, TableFormat.Csv);

    private static TabularData ParseDelimited(TextReader reader, char delimiter, string path, TableFormat fmt)
    {
        var records = ReadRecords(reader, delimiter).ToList();
        if (records.Count == 0) throw new InvalidDataException("The table is empty; a header row is required.");
        var header = records[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"column{i + 1}" : h.Trim()).ToArray();
        if (header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.Length)
            throw new InvalidDataException("The header contains duplicate column names.");
        var rows = records.Skip(1).Where(r => !(r.Length == 1 && string.IsNullOrEmpty(r[0])))
            .Select(r => { var row = new string?[header.Length]; for (int i = 0; i < header.Length && i < r.Length; i++) row[i] = r[i]; return row; })
            .ToList();
        return new TabularData { SourcePath = path, Format = fmt, Columns = header, Rows = rows };
    }

    /// <summary>RFC 4180 record reader (quoted fields, escaped quotes, embedded newlines).</summary>
    private static IEnumerable<string[]> ReadRecords(TextReader reader, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            any = true;
            char ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && field.Length == 0) { inQuotes = true; continue; }
            if (ch == delimiter) { fields.Add(field.ToString()); field.Clear(); continue; }
            if (ch == '\r') { if (reader.Peek() == '\n') reader.Read(); ch = '\n'; }
            if (ch == '\n')
            {
                fields.Add(field.ToString()); field.Clear();
                yield return [.. fields];
                fields.Clear(); any = false;
                continue;
            }
            field.Append(ch);
        }
        if (inQuotes) throw new InvalidDataException("Unterminated quoted field in delimited file.");
        if (any || field.Length > 0 || fields.Count > 0) { fields.Add(field.ToString()); yield return [.. fields]; }
    }

    private static TabularData ParseJson(string text, string path)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            // Accept {"data":[...]} / {"rows":[...]} / {"items":[...]} wrappers.
            var arr = root.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
            if (arr.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("JSON must be an array of objects or contain one.");
            root = arr.Value;
        }
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("JSON must be an array of objects.");
        return FromObjects(root.EnumerateArray().Select(e => e.Clone()), path, TableFormat.Json);
    }

    private static TabularData ParseJsonLines(TextReader reader, string path)
    {
        var items = new List<JsonElement>();
        int lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { using var d = JsonDocument.Parse(line); items.Add(d.RootElement.Clone()); }
            catch (JsonException ex) { throw new InvalidDataException($"Invalid JSON on line {lineNo}: {ex.Message}"); }
        }
        return FromObjects(items, path, TableFormat.JsonLines);
    }

    private static TabularData FromObjects(IEnumerable<JsonElement> objects, string path, TableFormat fmt)
    {
        var list = objects.ToList();
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in list)
        {
            if (o.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every JSON record must be an object.");
            foreach (var p in o.EnumerateObject()) if (seen.Add(p.Name)) columns.Add(p.Name);
        }
        var rows = list.Select(o =>
        {
            var row = new string?[columns.Count];
            foreach (var p in o.EnumerateObject())
            {
                int i = columns.FindIndex(c => string.Equals(c, p.Name, StringComparison.OrdinalIgnoreCase));
                row[i] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText()
                };
            }
            return row;
        }).ToList();
        return new TabularData { SourcePath = path, Format = fmt, Columns = columns, Rows = rows };
    }
}
