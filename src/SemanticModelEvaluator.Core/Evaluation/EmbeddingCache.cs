using System.Security.Cryptography;
using System.Text;

namespace SemanticModelEvaluator.Core.Evaluation;

/// <summary>
/// Disk cache of embeddings keyed by (model identity, role, text hash). Only vectors and hashes are stored — never text.
/// </summary>
public sealed class EmbeddingCache(string root)
{
    public string Root { get; } = root;

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private string FileFor(string modelKey, string role)
    {
        if (role is not ("Query" or "Document")) throw new ArgumentException("Unknown embedding role.", nameof(role));
        return Path.Combine(Root, Hash(modelKey)[..24], role + ".bin");
    }

    public Dictionary<string, float[]> Load(string modelKey, string role)
    {
        var path = FileFor(modelKey, role);
        var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        using (var br = new BinaryReader(File.OpenRead(path)))
        {
            if (br.ReadInt32() != 0x534D4543) throw new InvalidDataException("Embedding cache is corrupt. Clear it explicitly and retry.");
            int count = br.ReadInt32();
            if (count < 0 || count > br.BaseStream.Length / 8) throw new InvalidDataException("Invalid embedding cache entry count.");
            for (int i = 0; i < count; i++)
            {
                var key = br.ReadString();
                int dim = br.ReadInt32();
                if (dim < 1 || dim > 65536 || dim * sizeof(float) > br.BaseStream.Length - br.BaseStream.Position)
                    throw new InvalidDataException("Invalid embedding dimension in cache.");
                var v = new float[dim];
                for (int j = 0; j < dim; j++) v[j] = br.ReadSingle();
                map[key] = v;
            }
        }
        return map;
    }

    public void Save(string modelKey, string role, IReadOnlyDictionary<string, float[]> entries)
    {
        var path = FileFor(modelKey, role);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var bw = new BinaryWriter(File.Create(temp)))
        {
            bw.Write(0x534D4543);
            bw.Write(entries.Count);
            foreach (var (k, v) in entries)
            {
                bw.Write(k);
                bw.Write(v.Length);
                foreach (var f in v) bw.Write(f);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    public long SizeBytes() => Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    public void Clear()
    {
        if (!Directory.Exists(Root)) return;
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache root must not be a link.");
        foreach (var directory in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 24 || name.Any(c => !Uri.IsHexDigit(c)) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected entry in embedding cache; nothing outside recognized cache files will be deleted.");
            foreach (var role in new[] { "Query", "Document" })
            {
                var file = Path.Combine(directory, role + ".bin");
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
