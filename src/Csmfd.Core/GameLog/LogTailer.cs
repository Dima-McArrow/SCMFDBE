using System.Text;
using Csmfd.Protocol;

namespace Csmfd.Core.GameLog;

/// <summary>One complete line and the byte offset where it starts in the current file generation.</summary>
public readonly record struct LogLine(string Text, long Offset, Guid Generation);

/// <summary>
/// Incremental, non-blocking Game.log reader (spec 41.14). Reads only appended bytes, splits lines on
/// bytes (so every line has an exact start offset and split UTF-8 sequences are never decoded early),
/// opens with sharing that never blocks the game's writes/rotation and starts a new generation when
/// the file is replaced, truncated or continuity is lost. Poll() is cheap when nothing changed.
/// </summary>
public sealed class LogTailer
{
    public const int ChunkSize = 64 * 1024;
    public const int MaxLineBytes = 64 * 1024;

    private const int IdentityPrefixBytes = 256;

    private readonly string _path;
    private readonly int _maxBytesPerPoll;
    private readonly List<byte> _partial = new(1024);

    private FileIdentity? _identity;
    private long _offset;
    private bool _skippingLongLine;
    private bool _started;

    public LogTailer(string path, int maxBytesPerPoll = 4 * 1024 * 1024)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _maxBytesPerPoll = maxBytesPerPoll;
    }

    public Guid Generation { get; private set; } = Guid.NewGuid();

    public LogStatus Status { get; private set; } = LogStatus.Missing;

    public long Offset => _offset;

    public long SkippedLongLines { get; private set; }

    /// <summary>
    /// Current length of the file in this generation: the ATC watermark (spec 41.13). Anything at or after
    /// this offset was written after the call, even if it has not been parsed yet.
    /// </summary>
    public long ProbeLength()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? Math.Max(info.Length, _offset) : _offset;
        }
        catch (IOException)
        {
            return _offset;
        }
    }

    /// <summary>
    /// Reads newly appended data. The very first successful poll positions at end of file without
    /// emitting history (spec 41.14: never replay old joins/ATC as live). Returns true when the
    /// generation changed (file replaced/truncated) so the caller can reset dependent state.
    /// </summary>
    public bool Poll(Action<LogLine> onLine, Action<Guid> onReset)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentNullException.ThrowIfNull(onReset);

        FileStream stream;
        try
        {
            stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return MarkUnavailable(LogStatus.Missing, onReset);
        }
        catch (DirectoryNotFoundException)
        {
            return MarkUnavailable(LogStatus.Missing, onReset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MarkUnavailable(LogStatus.Unreadable, onReset);
        }

        using (stream)
        {
            try
            {
                return ReadFrom(stream, onLine, onReset);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return MarkUnavailable(LogStatus.Unreadable, onReset);
            }
        }
    }

    private bool ReadFrom(FileStream stream, Action<LogLine> onLine, Action<Guid> onReset)
    {
        var identity = FileIdentity.Read(_path, stream);
        var length = stream.Length;
        var generationChanged = false;

        if (!_started)
        {
            // First sight of the file: start at EOF with unknown history.
            _started = true;
            _identity = identity;
            _offset = length;
            Status = LogStatus.Ready;
            return false;
        }

        if (_identity is null || !_identity.Value.SameFile(identity) || length < _offset)
        {
            // Replaced, rotated or truncated: new generation, read the new file from the start.
            StartGeneration(identity);
            generationChanged = true;
            onReset(Generation); // before any line of the new generation
        }
        else
        {
            // Same file; remember the (possibly longer) prefix for the next comparison.
            _identity = identity;
        }

        var budget = _maxBytesPerPoll;
        var buffer = new byte[ChunkSize];
        stream.Seek(_offset, SeekOrigin.Begin);
        while (budget > 0)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, budget));
            if (read == 0)
            {
                break;
            }

            Consume(buffer.AsSpan(0, read), onLine);
            budget -= read;
        }

        Status = stream.Length - _offset > _maxBytesPerPoll / 4 ? LogStatus.CatchingUp : LogStatus.Ready;
        return generationChanged;
    }

    private void Consume(ReadOnlySpan<byte> data, Action<LogLine> onLine)
    {
        var lineStart = _offset - _partial.Count;
        while (!data.IsEmpty)
        {
            var newline = data.IndexOf((byte)'\n');
            var chunk = newline < 0 ? data : data[..newline];

            if (!_skippingLongLine)
            {
                if (_partial.Count + chunk.Length > MaxLineBytes)
                {
                    // Oversized line: drop it up to the next newline, count it (spec 41.14).
                    _skippingLongLine = true;
                    _partial.Clear();
                    SkippedLongLines++;
                }
                else
                {
                    foreach (var b in chunk)
                    {
                        _partial.Add(b);
                    }
                }
            }

            if (newline < 0)
            {
                _offset += data.Length;
                return;
            }

            _offset += newline + 1;
            if (!_skippingLongLine)
            {
                onLine(new LogLine(Decode(), lineStart, Generation));
            }

            _skippingLongLine = false;
            _partial.Clear();
            lineStart = _offset;
            data = data[(newline + 1)..];
        }
    }

    private string Decode()
    {
        var count = _partial.Count;
        if (count > 0 && _partial[count - 1] == (byte)'\r')
        {
            count--;
        }

        // Invalid sequences become U+FFFD; the original file is never modified.
        return Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_partial)[..count]);
    }

    private void StartGeneration(FileIdentity identity)
    {
        Generation = Guid.NewGuid();
        _identity = identity;
        _offset = 0;
        _partial.Clear();
        _skippingLongLine = false;
    }

    private bool MarkUnavailable(LogStatus status, Action<Guid> onReset)
    {
        Status = status;
        if (!_started)
        {
            // Nothing seen yet: when the file appears later, it is new and is read from the start.
            _started = true;
            _identity = null;
            _offset = 0;
            return false;
        }

        if (_identity is null)
        {
            return false;
        }

        // Continuity is lost: the next file we see is a new generation.
        _identity = null;
        _partial.Clear();
        Generation = Guid.NewGuid();
        _offset = 0;
        onReset(Generation);
        return true;
    }

    /// <summary>
    /// Creation time + the first bytes of the file (the first Game.log line carries the log start time).
    /// The prefix is the main guard: Windows file tunneling can give a re-created file the old creation time.
    /// </summary>
    private readonly record struct FileIdentity(DateTime CreatedUtc, byte[] Prefix)
    {
        public static FileIdentity Read(string path, FileStream stream)
        {
            var prefix = new byte[(int)Math.Min(IdentityPrefixBytes, stream.Length)];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(prefix);
            return new FileIdentity(File.GetCreationTimeUtc(path), prefix);
        }

        /// <summary>A growing file keeps its creation time; its prefix may only get longer, never different.</summary>
        public bool SameFile(FileIdentity current) =>
            CreatedUtc == current.CreatedUtc
            && current.Prefix.Length >= Prefix.Length
            && current.Prefix.AsSpan().StartsWith(Prefix);
    }
}
