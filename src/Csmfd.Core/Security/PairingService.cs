using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Csmfd.Protocol;

namespace Csmfd.Core.Security;

/// <summary>A paired iPad. Only the SHA-256 of its token is ever stored (spec 41.5 item 7).</summary>
public sealed record TrustedDevice(Guid Id, string Name, string TokenHash, DateTimeOffset PairedAt);

/// <summary>Persistence for trusted devices (a user-only file in %LocalAppData% on Windows).</summary>
public interface ITrustStore
{
    IReadOnlyList<TrustedDevice> Load();

    void Save(IReadOnlyList<TrustedDevice> devices);
}

public enum PairOutcome
{
    Paired,
    Invalid,
    Locked,
    Unavailable,
}

public sealed record PairResult(PairOutcome Outcome, string? Token = null, TrustedDevice? Device = null);

/// <summary>
/// First trust and token validation (spec 41.5). The pairing window is opened only locally on the PC,
/// lasts 120 s, uses a one-time CSPRNG code and allows 5 failed attempts in total across all clients,
/// after which it closes and can only be reopened locally.
/// </summary>
public sealed class PairingService
{
    public const int MaxFailedAttempts = 5;

    public static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(120);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ITrustStore _store;
    private readonly List<TrustedDevice> _devices;

    private string? _code;
    private long _openedAt;
    private int _failures;

    public PairingService(TimeProvider time, ITrustStore store)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _devices = [.. store.Load()];
    }

    /// <summary>Raised outside the lock with the id of a revoked device.</summary>
    public event Action<Guid>? DeviceRevoked;

    public IReadOnlyList<TrustedDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices];
            }
        }
    }

    public bool IsWindowOpen
    {
        get
        {
            lock (_gate)
            {
                return IsOpenLocked();
            }
        }
    }

    /// <summary>Opens (or restarts) the local pairing window and returns the new code.</summary>
    public string OpenWindow()
    {
        lock (_gate)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            _openedAt = _time.GetTimestamp();
            _failures = 0;
            return _code;
        }
    }

    public void CloseWindow()
    {
        lock (_gate)
        {
            _code = null;
        }
    }

    public PairResult TryPair(string code, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(deviceName);

        lock (_gate)
        {
            if (!IsOpenLocked())
            {
                return new PairResult(PairOutcome.Unavailable);
            }

            var matches = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(code),
                Encoding.ASCII.GetBytes(_code!));

            if (!matches)
            {
                if (++_failures >= MaxFailedAttempts)
                {
                    _code = null;
                    return new PairResult(PairOutcome.Locked);
                }

                return new PairResult(PairOutcome.Invalid);
            }

            var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TextRules.TokenByteLength));
            var device = new TrustedDevice(Guid.NewGuid(), deviceName, HashToken(token), _time.GetUtcNow());
            _devices.Add(device);
            _store.Save([.. _devices]);
            _code = null;
            return new PairResult(PairOutcome.Paired, token, device);
        }
    }

    /// <summary>Returns the device owning this token. Compares against every hash in constant time.</summary>
    public TrustedDevice? Authenticate(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var candidate = Convert.FromHexString(HashToken(token));
        TrustedDevice? match = null;
        lock (_gate)
        {
            foreach (var device in _devices)
            {
                // No early exit: timing must not reveal which entry matched.
                if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(device.TokenHash)))
                {
                    match = device;
                }
            }
        }

        return match;
    }

    public bool Revoke(Guid deviceId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.RemoveAll(d => d.Id == deviceId) > 0;
            if (removed)
            {
                _store.Save([.. _devices]);
            }
        }

        if (removed)
        {
            DeviceRevoked?.Invoke(deviceId);
        }

        return removed;
    }

    public void RevokeAll()
    {
        List<Guid> revoked;
        lock (_gate)
        {
            revoked = [.. _devices.Select(d => d.Id)];
            _devices.Clear();
            _store.Save([]);
        }

        foreach (var id in revoked)
        {
            DeviceRevoked?.Invoke(id);
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private bool IsOpenLocked()
    {
        if (_code is null)
        {
            return false;
        }

        if (_time.GetElapsedTime(_openedAt) >= WindowDuration)
        {
            _code = null;
            return false;
        }

        return true;
    }
}
