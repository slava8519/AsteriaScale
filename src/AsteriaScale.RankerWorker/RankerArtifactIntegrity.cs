using System.Security.Cryptography;

namespace AsteriaScale.RankerWorker;

internal static class RankerArtifactIntegrity
{
    internal static RankerArtifactReadLease AcquireReadLeaseAndValidate(
        string path,
        long expectedLength,
        string expectedSha256,
        string errorCode,
        string description)
    {
        FileStream? stream = null;
        try
        {
            // FileShare.Read is intentional: after authentication, another
            // process may still read the artifact, but it cannot replace,
            // truncate or delete the path while a native loader reopens it.
            // That exclusion is Windows FileShare semantics. On macOS .NET
            // maps FileShare to an advisory flock(2): only another opener that
            // asks for an exclusive lock is refused, while any other process
            // can still replace, truncate or delete the file. ONNX Runtime
            // later reopens the file by path and maps external data
            // copy-on-write, so on macOS an artifact is "validated at load"
            // rather than pinned for the session (see the worker README).
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            long actualLength = stream.Length;
            if (actualLength != expectedLength)
            {
                throw new RankerException(
                    errorCode,
                    $"The pinned {description} has length {actualLength}, expected {expectedLength}: '{path}'.");
            }

            string actualSha256 = Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new RankerException(
                    errorCode,
                    $"The pinned {description} SHA-256 is {actualSha256}, expected {expectedSha256}: '{path}'.");
            }

            stream.Position = 0;
            RankerArtifactReadLease lease = new(path, stream);
            stream = null;
            return lease;
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException(
                errorCode,
                $"Could not open or hash the pinned {description} '{path}': {ex.Message}",
                ex);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal static byte[] ReadAllBytesAndValidate(
        string path,
        long expectedLength,
        string expectedSha256,
        string errorCode,
        string description)
    {
        using RankerArtifactReadLease lease = AcquireReadLeaseAndValidate(
            path,
            expectedLength,
            expectedSha256,
            errorCode,
            description);
        return lease.ReadAllBytes();
    }

    internal static void Validate(
        string path,
        long expectedLength,
        string expectedSha256,
        string errorCode,
        string description)
    {
        using RankerArtifactReadLease lease = AcquireReadLeaseAndValidate(
            path,
            expectedLength,
            expectedSha256,
            errorCode,
            description);
    }
}

internal sealed class RankerArtifactReadLease : IDisposable
{
    private FileStream? _stream;

    internal RankerArtifactReadLease(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    internal string Path { get; }

    internal byte[] ReadAllBytes()
    {
        FileStream stream = _stream ?? throw new ObjectDisposedException(
            nameof(RankerArtifactReadLease));
        if (stream.Length > int.MaxValue)
        {
            throw new IOException(
                $"The pinned artifact is too large to buffer: '{Path}'.");
        }

        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        stream.Position = 0;
        return bytes;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
