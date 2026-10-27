using Csmfd.Server.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Server;

public sealed class DebugLoggingTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 16, 0, 0, TimeSpan.Zero));
    private readonly FileLoggerProvider _provider = new(Path.Combine(Path.GetTempPath(), "csmfd-debug-logging-tests"));
    private readonly DebugLogging _debug;
    private int _changes;

    public DebugLoggingTests()
    {
        _debug = new DebugLogging(_provider, _time, NullLogger.Instance);
        _debug.Changed += () => _changes++;
    }

    public void Dispose()
    {
        _debug.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public void IsOffAtStart()
    {
        Assert.Null(_debug.Until);
        Assert.Equal(LogLevel.Information, _provider.MinimumLevel);
    }

    [Fact]
    public void TurnsItselfOffAfterFifteenMinutes()
    {
        _debug.Enable();
        Assert.Equal(LogLevel.Debug, _provider.MinimumLevel);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(15), _debug.Until);

        _time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(LogLevel.Debug, _provider.MinimumLevel);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_debug.Until);
        Assert.Equal(LogLevel.Information, _provider.MinimumLevel);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void EnablingAgainRestartsTheWindow()
    {
        _debug.Enable();
        _time.Advance(TimeSpan.FromMinutes(10));
        _debug.Enable();

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(LogLevel.Debug, _provider.MinimumLevel);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(LogLevel.Information, _provider.MinimumLevel);
    }

    [Fact]
    public void CanBeSwitchedOffEarly()
    {
        _debug.Enable();
        _debug.Disable();

        Assert.Null(_debug.Until);
        Assert.Equal(LogLevel.Information, _provider.MinimumLevel);

        _time.Advance(TimeSpan.FromMinutes(20));
        _debug.Disable();
        Assert.Equal(2, _changes);
    }
}
