using System.Globalization;
using System.Security.Cryptography;
using Parquet;
using Parquet.Schema;
using SemanticModelEvaluator.Core.Datasets;

namespace SemanticModelEvaluator.Providers.Datasets;

/// <summary>Data-only loaders. Repeated Parquet fields require an explicit upstream flattening, never guessed joins.</summary>
public static class DatasetTableReader
{
    public const long MaxDecodedGroupBytes = 256L * 1024 * 1024;
    public const int MaxGroupRows = 250_000;

    public static async Task<IReadOnlyList<string>> ColumnsAsync(string path, CancellationToken ct)
    {
        if (!path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            return (await Task.Run(() => TabularReader.Read(path), ct).ConfigureAwait(false)).Columns;
        await using var stream = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return reader.Schema.GetDataFields().Select(f => f.Path.ToString()).ToArray();
    }

    public static async Task<TabularData> ReadAsync(string path, IReadOnlyCollection<string> columns, int limit,
        string assetDirectory, CancellationToken ct)
    {
        if (limit is < 1 or > MaxGroupRows) throw new ArgumentOutOfRangeException(nameof(limit), $"Select 1-{MaxGroupRows} rows per input file.");
        if (!path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            var table = await Task.Run(() => TabularReader.Read(path), ct).ConfigureAwait(false);
            foreach (var column in columns)
                if (table.IndexOf(column) < 0) throw new InvalidDataException($"Mapped column '{column}' does not exist.");
            return new() { SourcePath = path, Format = table.Format, Columns = table.Columns, Rows = table.Rows.Take(limit).ToArray() };
        }
        await using var input = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(input, cancellationToken: ct).ConfigureAwait(false);
        var all = reader.Schema.GetDataFields().ToDictionary(f => f.Path.ToString(), StringComparer.Ordinal);
        var fields = columns.Select(c => all.TryGetValue(c, out var f) ? f : throw new InvalidDataException($"Mapped Parquet column '{c}' does not exist.")).ToArray();
        if (fields.Any(f => f.MaxRepetitionLevel > 0))
            throw new NotSupportedException("Repeated/list Parquet columns need an explicit flattening recipe. Export static flat rows with stable IDs; repository scripts are never executed.");
        var rows = new List<string?[]>();
        for (int groupIndex = 0; groupIndex < reader.RowGroupCount && rows.Count < limit; groupIndex++)
        {
            ct.ThrowIfCancellationRequested();
            using var group = reader.OpenRowGroupReader(groupIndex);
            if (group.RowCount > MaxGroupRows ||
                fields.Sum(f => group.GetMetadata(f)?.MetaData?.TotalUncompressedSize ?? 0) > MaxDecodedGroupBytes)
                throw new InvalidDataException("Parquet row group exceeds the bounded loader's memory budget. Choose a smaller shard or export a smaller static subset.");
            int count = checked((int)group.RowCount), take = Math.Min(count, limit - rows.Count);
            var values = new List<string?[]>();
            foreach (var field in fields)
                values.Add(await ReadColumnAsync(group, field, count, take, assetDirectory, ct).ConfigureAwait(false));
            for (int r = 0; r < take; r++) rows.Add(values.Select(c => c[r]).ToArray());
        }
        return new() { SourcePath = path, Format = TableFormat.JsonLines, Columns = columns.ToArray(), Rows = rows };
    }

    private static async Task<string?[]> ReadColumnAsync(ParquetRowGroupReader group, DataField field, int count,
        int take, string assets, CancellationToken ct)
    {
        var type = field.ClrType;
        if (type == typeof(string) || type == typeof(ReadOnlyMemory<char>))
            return await RawAsync<ReadOnlyMemory<char>>(v => v.ToString()).ConfigureAwait(false);
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>))
            return await RawAsync<ReadOnlyMemory<byte>>(v => SaveImage(v.Span, assets)).ConfigureAwait(false);
        if (type == typeof(int)) return await RawAsync<int>(v => v.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (type == typeof(long)) return await RawAsync<long>(v => v.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (type == typeof(float)) return await RawAsync<float>(v => v.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (type == typeof(double)) return await RawAsync<double>(v => v.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (type == typeof(bool)) return await RawAsync<bool>(v => v ? "true" : "false").ConfigureAwait(false);
        if (type == typeof(decimal)) return await RawAsync<decimal>(v => v.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        throw new NotSupportedException($"Parquet field '{field.Path}' has unsupported type '{type.Name}'. Select scalar text/number or embedded image columns.");

        async Task<string?[]> RawAsync<T>(Func<T, string> convert) where T : struct
        {
            var buffer = new T[count];
            int[]? levels = field.MaxDefinitionLevel > 0 ? new int[count] : null;
            await group.ReadRawAsync<T>(field, buffer.AsMemory(), levels?.AsMemory(), null, ct).ConfigureAwait(false);
            var values = new string?[take];
            int source = 0;
            for (int i = 0; i < take; i++)
                if (levels is null || levels[i] == field.MaxDefinitionLevel) values[i] = convert(buffer[source++]);
            return values;
        }
    }

    public static string SaveImage(ReadOnlySpan<byte> bytes, string directory)
    {
        if (bytes.Length is < 4 or > 20 * 1024 * 1024) throw new InvalidDataException("Image is empty or exceeds 20 MB.");
        string extension = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
            : bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? ".jpg"
            : bytes.Length > 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? ".webp"
            : throw new NotSupportedException("Only embedded PNG, JPEG and WebP images are accepted. HTML/SVG and remote image URLs are not loaded.");
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension);
        Directory.CreateDirectory(directory);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }
}
