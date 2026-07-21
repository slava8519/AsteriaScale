using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AsteriaScale.RankerWorker;

internal sealed record GlossVector(ulong GlossHash, float Scale, sbyte[] Values);

/// <summary>
/// Random-access reader for the precomputed MiniLM gloss embeddings
/// (tools/build-gloss-embeddings.py, roadmap task #7). Only the key→offset
/// table lives in memory; per-key vector blocks are read from disk on demand,
/// so the index adds no bulk RAM. Glosses are matched by an FNV-1a hash of the
/// normalized definition, which survives runtime grouping and dedup.
/// </summary>
internal sealed class GlossEmbeddingIndex : IDisposable
{
    private readonly FileStream _stream;
    private readonly Dictionary<string, long> _offsets;
    private readonly object _readLock = new();

    public GlossEmbeddingIndex(string path)
    {
        _stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.RandomAccess);
        using StreamReader headerReader = new(
            _stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4 * 1024,
            leaveOpen: true);
        string headerLine = ReadHeaderLine(_stream);
        using JsonDocument header = JsonDocument.Parse(headerLine);
        Format = header.RootElement.GetProperty("format").GetInt32();
        Dimensions = header.RootElement.GetProperty("dimensions").GetInt32();
        ModelId = header.RootElement.GetProperty("model").GetString() ?? "unknown";
        Revision = header.RootElement.GetProperty("revision").GetString() ?? "unknown";
        Normalized = header.RootElement.GetProperty("normalized").GetBoolean();
        SequenceLength = header.RootElement.GetProperty("sequenceLength").GetInt32();
        SenseFileSha256 =
            header.RootElement.GetProperty("senseFileSha256").GetString() ?? "unknown";
        DeclaredKeyCount = header.RootElement.GetProperty("keys").GetInt32();
        DeclaredVectorCount = header.RootElement.GetProperty("vectors").GetInt32();
        if (Dimensions is < 16 or > 4096)
        {
            throw new RankerException(
                "invalid_embedding_index",
                $"Unsupported embedding dimensionality {Dimensions}.");
        }

        _offsets = BuildOffsetTable(_stream, Dimensions);
    }

    public int Format { get; }
    public int Dimensions { get; }
    public string ModelId { get; }
    public string Revision { get; }
    public bool Normalized { get; }
    public int SequenceLength { get; }
    public string SenseFileSha256 { get; }
    public int DeclaredKeyCount { get; }
    public int DeclaredVectorCount { get; }
    public int KeyCount => _offsets.Count;

    public bool TryReadVectors(string key, out IReadOnlyList<GlossVector> vectors)
    {
        vectors = [];
        if (!_offsets.TryGetValue(key, out long offset))
        {
            return false;
        }

        lock (_readLock)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            using BinaryReader reader = new(_stream, Encoding.UTF8, leaveOpen: true);
            int count = reader.ReadInt32();
            if (count is < 0 or > 512)
            {
                return false;
            }

            var result = new GlossVector[count];
            for (int index = 0; index < count; index++)
            {
                ulong hash = reader.ReadUInt64();
                float scale = reader.ReadSingle();
                byte[] raw = reader.ReadBytes(Dimensions);
                if (raw.Length != Dimensions)
                {
                    return false;
                }

                sbyte[] values = new sbyte[Dimensions];
                Buffer.BlockCopy(raw, 0, values, 0, Dimensions);
                result[index] = new GlossVector(hash, scale, values);
            }

            vectors = result;
            return true;
        }
    }

    /// <summary>Cosine similarity against a normalized float query.</summary>
    public static double Similarity(float[] query, GlossVector vector)
    {
        double sum = 0;
        for (int index = 0; index < query.Length; index++)
        {
            sum += query[index] * vector.Values[index];
        }

        return sum * vector.Scale;
    }

    /// <summary>Must match normalize_gloss + fnv1a64 in the build tool.</summary>
    public static ulong HashNormalizedGloss(string gloss)
    {
        string normalized = string.Join(
            " ",
            gloss.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        ulong digest = 0xCBF29CE484222325UL;
        foreach (byte value in Encoding.UTF8.GetBytes(normalized))
        {
            digest = (digest ^ value) * 0x100000001B3UL;
        }

        return digest;
    }

    private static string ReadHeaderLine(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        List<byte> bytes = new(2048);
        int value;
        while ((value = stream.ReadByte()) >= 0 && value != '\n')
        {
            bytes.Add((byte)value);
            if (bytes.Count > 64 * 1024)
            {
                throw new RankerException(
                    "invalid_embedding_index",
                    "The embedding index header is not terminated.");
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static Dictionary<string, long> BuildOffsetTable(
        FileStream stream,
        int dimensions)
    {
        Dictionary<string, long> offsets = new(StringComparer.Ordinal);
        long position = stream.Position;
        long length = stream.Length;
        Span<byte> small = stackalloc byte[6];
        byte[] keyBuffer = new byte[1024];
        while (position + 6 <= length)
        {
            stream.Seek(position, SeekOrigin.Begin);
            if (stream.Read(small[..2]) != 2)
            {
                break;
            }

            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(small[..2]);
            if (keyLength == 0 || keyLength > keyBuffer.Length ||
                stream.Read(keyBuffer, 0, keyLength) != keyLength ||
                stream.Read(small[..4]) != 4)
            {
                break;
            }

            int vectorCount = BinaryPrimitives.ReadInt32LittleEndian(small[..4]);
            if (vectorCount is < 0 or > 512)
            {
                break;
            }

            string key = Encoding.UTF8.GetString(keyBuffer, 0, keyLength);
            long vectorsOffset = position + 2 + keyLength;
            offsets[key] = vectorsOffset;
            position = vectorsOffset + 4 + (long)vectorCount * (8 + 4 + dimensions);
        }

        return offsets;
    }

    public void Dispose() => _stream.Dispose();
}
