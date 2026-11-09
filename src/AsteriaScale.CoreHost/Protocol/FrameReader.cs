using System.Buffers;

namespace AsteriaScale.CoreHost.Protocol;

/// <summary>
/// Bounded line reader: a frame is the bytes up to '\n'. It blocks in read(2)
/// on a dedicated background thread, so the process can still exit while the
/// read is pending (SIGTERM, a fatal error).
/// </summary>
internal sealed class FrameReader(Stream input, int maxFrameBytes)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;

    public enum Result
    {
        Frame,
        Eof,
        TooLong,
    }

    public Result Read(out ReadOnlyMemory<byte> frame)
    {
        _line.Clear();
        while (true)
        {
            if (_start == _end)
            {
                int read;
                try
                {
                    read = input.Read(_buffer, 0, _buffer.Length);
                }
                catch (IOException)
                {
                    read = 0;
                }

                if (read == 0)
                {
                    // A partial last line without '\n' is not a frame.
                    frame = default;
                    return Result.Eof;
                }

                _start = 0;
                _end = read;
            }

            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int take = (newline < 0 ? _end : newline) - _start;
            if (_line.WrittenCount + take > maxFrameBytes)
            {
                frame = default;
                return Result.TooLong;
            }

            _line.Write(_buffer.AsSpan(_start, take));
            if (newline < 0)
            {
                _start = _end;
                continue;
            }

            _start = newline + 1;
            frame = _line.WrittenMemory;
            return Result.Frame;
        }
    }
}
