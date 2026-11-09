using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AsteriaScale.CoreHost.Protocol;

/// <summary>An outbound frame over <see cref="WireProtocol.MaxOutboundFrameBytes"/>.</summary>
internal sealed class OutboundFrameTooLargeException(int bytes)
    : Exception($"An outbound frame of {bytes} bytes exceeds the {WireProtocol.MaxOutboundFrameBytes}-byte limit.");

/// <summary>
/// The only writer of the protocol stream. Producers serialize a frame whole
/// and queue it; one dedicated thread writes frames in queue order, so frames
/// never interleave and the order on the wire is the order of the calls. The
/// per-request gate makes "exactly one terminal, events only before it" a
/// property of the writer rather than of every handler. Enqueueing never
/// blocks: a Core thread must not stall on a peer that stopped reading, so
/// the queue is bounded in bytes and overflowing it ends the process (exit 5).
/// </summary>
internal sealed class FrameWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = WireJson.Encoder,
        Indented = false,
        SkipValidation = false,
    };

    private readonly Stream _output;
    private readonly Action<int, string> _fatal;
    private readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>());
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _queuedBytes;
    private long _framesWritten;
    private long _droppedEvents;
    private volatile bool _failed;

    public FrameWriter(Stream output, Action<int, string> fatal)
    {
        _output = output;
        _fatal = fatal;
        Thread thread = new(Run) { IsBackground = true, Name = "corehost-writer" };
        thread.Start();
    }

    /// <summary>Completes when every queued frame was written after <see cref="Complete"/>.</summary>
    public Task Drained => _drained.Task;

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public void Complete()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool Respond<T>(RequestState request, T result, JsonTypeInfo<T> info) =>
        Terminal(request, Serialize(request.Id, null, null, ok: true, result, info, error: null));

    public bool Fail(RequestState request, ErrorBody error) =>
        Terminal(request, Serialize<EmptyResult>(request.Id, null, null, ok: false, null, null, error));

    /// <summary>A request-level error for an id that never became a request.</summary>
    public void FailUnadmitted(long id, ErrorBody error) =>
        Enqueue(Serialize<EmptyResult>(id, null, null, ok: false, null, null, error));

    /// <summary>
    /// An event for a request (dropped and counted once its terminal is
    /// written) or, with no request, an unsolicited one.
    /// </summary>
    public bool Event<T>(RequestState? request, string name, T payload, JsonTypeInfo<T> info)
    {
        byte[] frame = Serialize(null, name, request?.Id, ok: true, payload, info, error: null);
        if (request is null)
        {
            Enqueue(frame);
            return true;
        }

        lock (request.Gate)
        {
            if (request.Terminal)
            {
                Interlocked.Increment(ref _droppedEvents);
                return false;
            }

            Enqueue(frame);
            return true;
        }
    }

    /// <summary>Test command only: corrupts the stream on purpose.</summary>
    public void Raw(byte[] bytes) => Enqueue(bytes);

    private bool Terminal(RequestState request, byte[] frame)
    {
        lock (request.Gate)
        {
            if (request.Terminal)
            {
                return false;
            }

            request.Terminal = true;
            Enqueue(frame);
            return true;
        }
    }

    private void Enqueue(byte[] frame)
    {
        if (_failed)
        {
            return;
        }

        if (Interlocked.Add(ref _queuedBytes, frame.Length) > WireProtocol.MaxQueuedOutboundBytes)
        {
            // The peer stopped reading stdout. Blocking Core threads on it
            // would turn a stuck app into a stuck Core, so the host gives up.
            _failed = true;
            _fatal(ExitCodes.OutputOverflow, "the peer stopped reading: more than 16 MiB of frames queued");
            return;
        }

        try
        {
            _queue.Add(frame);
        }
        catch (InvalidOperationException)
        {
            // Completed: the host is exiting and the frame has no reader.
            Interlocked.Add(ref _queuedBytes, -frame.Length);
        }
    }

    private static byte[] Serialize<T>(
        long? id,
        string? eventName,
        long? reference,
        bool ok,
        T? body,
        JsonTypeInfo<T>? info,
        ErrorBody? error)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", WireProtocol.Version);
            if (eventName is null)
            {
                writer.WriteNumber("id", id!.Value);
                writer.WriteBoolean("ok", ok);
                if (error is null)
                {
                    writer.WritePropertyName("result");
                    JsonSerializer.Serialize(writer, body!, info!);
                }
                else
                {
                    writer.WritePropertyName("error");
                    JsonSerializer.Serialize(writer, error, WireJson.Strict.ErrorBody);
                }
            }
            else
            {
                writer.WriteString("event", eventName);
                if (reference is { } referenced)
                {
                    writer.WriteNumber("ref", referenced);
                }

                writer.WritePropertyName("payload");
                JsonSerializer.Serialize(writer, body!, info!);
            }

            writer.WriteEndObject();
        }

        if (buffer.WrittenCount + 1 > WireProtocol.MaxOutboundFrameBytes)
        {
            throw new OutboundFrameTooLargeException(buffer.WrittenCount + 1);
        }

        byte[] frame = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(frame);
        frame[^1] = (byte)'\n';
        return frame;
    }

    private void Run()
    {
        try
        {
            foreach (byte[] frame in _queue.GetConsumingEnumerable())
            {
                try
                {
                    _output.Write(frame, 0, frame.Length);
                    if (_queue.Count == 0)
                    {
                        _output.Flush();
                    }
                }
                catch (IOException)
                {
                    // EPIPE: nobody reads the answers any more.
                    _failed = true;
                    _fatal(ExitCodes.PeerGone, "stdout is closed (EPIPE)");
                    return;
                }

                Interlocked.Add(ref _queuedBytes, -frame.Length);
                Interlocked.Increment(ref _framesWritten);
            }
        }
        catch (Exception exception)
        {
            _failed = true;
            _fatal(ExitCodes.Internal, "the frame writer failed: " + exception.Message);
        }
        finally
        {
            _drained.TrySetResult();
        }
    }
}
