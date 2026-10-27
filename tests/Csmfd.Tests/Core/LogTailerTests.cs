using System.Text;
using Csmfd.Core.GameLog;
using Csmfd.Protocol;

namespace Csmfd.Tests.Core;

public sealed class LogTailerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "csmfd-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly List<LogLine> _lines = [];
    private readonly List<Guid> _resets = [];

    public LogTailerTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "Game.log");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void StartsAtEndOfExistingFileAndReadsOnlyAppendedLines()
    {
        Write("<t0> old line\r\n");
        var tailer = new LogTailer(_path);
        Poll(tailer);
        Assert.Empty(_lines);
        Assert.Equal(LogStatus.Ready, tailer.Status);

        Append("<t1> first\r\n<t2> second\r\n");
        Poll(tailer);

        Assert.Equal(["<t1> first", "<t2> second"], _lines.Select(l => l.Text));
        var firstOffset = Encoding.UTF8.GetByteCount("<t0> old line\r\n");
        Assert.Equal(firstOffset, _lines[0].Offset);
        Assert.Equal(firstOffset + Encoding.UTF8.GetByteCount("<t1> first\r\n"), _lines[1].Offset);
        Assert.Empty(_resets);
    }

    [Fact]
    public void WaitsForCompleteLineAndKeepsSplitUtf8Intact()
    {
        Write("<t0> start\n");
        var tailer = new LogTailer(_path);
        Poll(tailer);

        var bytes = Encoding.UTF8.GetBytes("<t1> Кэп 🚀\n");
        AppendBytes(bytes[..9]); // ends in the middle of a Cyrillic character
        Poll(tailer);
        Assert.Empty(_lines);

        AppendBytes(bytes[9..]);
        Poll(tailer);
        Assert.Equal("<t1> Кэп 🚀", Assert.Single(_lines).Text);
    }

    [Fact]
    public void TruncationStartsANewGenerationFromTheBeginning()
    {
        Write("<t0> a fairly long first line of the old file\n");
        var tailer = new LogTailer(_path);
        Poll(tailer);
        var first = tailer.Generation;

        Write("<n0> new\n");
        Poll(tailer);

        Assert.Equal([tailer.Generation], _resets);
        Assert.NotEqual(first, tailer.Generation);
        Assert.Equal("<n0> new", Assert.Single(_lines).Text);
        Assert.Equal(0, _lines[0].Offset);
        Assert.Equal(tailer.Generation, _lines[0].Generation);
    }

    [Fact]
    public void ReplacedFileWithDifferentStartIsANewGenerationEvenIfLonger()
    {
        Write("<2026-10-26T15:48:45.358Z> Log started A\n");
        var tailer = new LogTailer(_path);
        Poll(tailer);

        // Same name re-created (Windows may even keep the creation time): prefix differs.
        Write("<2026-10-27T09:00:00.000Z> Log started B\n<b1> more content than before\n");
        Poll(tailer);

        Assert.Single(_resets);
        Assert.Equal(["<2026-10-27T09:00:00.000Z> Log started B", "<b1> more content than before"], _lines.Select(l => l.Text));
    }

    [Fact]
    public void MissingFileThenCreatedIsReadFromTheStart()
    {
        var tailer = new LogTailer(_path);
        Poll(tailer);
        Assert.Equal(LogStatus.Missing, tailer.Status);

        Write("<t0> game started\n");
        Poll(tailer);

        Assert.Equal("<t0> game started", Assert.Single(_lines).Text);
        Assert.Equal(LogStatus.Ready, tailer.Status);
    }

    [Fact]
    public void OversizedLineIsSkippedAndCounted()
    {
        Write("<t0> start\n");
        var tailer = new LogTailer(_path);
        Poll(tailer);

        Append("<t1> " + new string('x', LogTailer.MaxLineBytes + 10) + "\n<t2> after\n");
        Poll(tailer);

        Assert.Equal("<t2> after", Assert.Single(_lines).Text);
        Assert.Equal(1, tailer.SkippedLongLines);
    }

    [Fact]
    public void LargeBacklogIsReadInBoundedSlices()
    {
        Write("<t0> start\n");
        var tailer = new LogTailer(_path, maxBytesPerPoll: 1024);
        Poll(tailer);

        Append(string.Concat(Enumerable.Range(0, 200).Select(i => $"<t{i:D3}> line\n")));
        Poll(tailer);
        Assert.Equal(LogStatus.CatchingUp, tailer.Status);
        Assert.InRange(_lines.Count, 1, 199);

        while (tailer.Status == LogStatus.CatchingUp)
        {
            Poll(tailer);
        }

        Assert.Equal(200, _lines.Count);
        Assert.Equal("<t199> line", _lines[^1].Text);
    }

    [Fact]
    public void WatermarkIsTheCurrentFileLength()
    {
        Write("<t0> start\n");
        var tailer = new LogTailer(_path);
        Poll(tailer);

        Append("<t1> unread yet\n");

        Assert.Equal(new FileInfo(_path).Length, tailer.ProbeLength());
        Assert.True(tailer.ProbeLength() > tailer.Offset);
    }

    private void Poll(LogTailer tailer) => tailer.Poll(_lines.Add, _resets.Add);

    private void Write(string text) => File.WriteAllText(_path, text);

    private void Append(string text) => File.AppendAllText(_path, text);

    private void AppendBytes(byte[] bytes)
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }
}
