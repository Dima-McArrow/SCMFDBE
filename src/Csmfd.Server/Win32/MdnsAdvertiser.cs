using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Csmfd.Server.Win32;

/// <summary>
/// Announces _csmfd._tcp with DnsServiceRegister (Windows 10+) on the chosen interface only
/// (spec 41.6). TXT: v=2, tls=1, id=&lt;serverId&gt;, name=&lt;displayName&gt;. Discovery is a convenience:
/// the fingerprint is never trusted from mDNS, and manual IP entry works when this fails.
/// </summary>
internal sealed unsafe partial class MdnsAdvertiser : IDisposable
{
    private const uint DnsRequestPending = 9506;
    private const uint DnsQueryRequestVersion1 = 1;

    private readonly ILogger _logger;
    private nint _instance;
    private DnsServiceRegisterRequest* _request;
    private bool _registered;

    private MdnsAdvertiser(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Returns null (and logs) when registration is not possible.</summary>
    public static MdnsAdvertiser? TryStart(string displayName, Guid serverId, IPAddress address, int interfaceIndex, int port, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(logger);

        var advertiser = new MdnsAdvertiser(logger);
        try
        {
            advertiser.Register(displayName, serverId, address, interfaceIndex, port);
            return advertiser;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            LogFailed(logger, ex.Message);
            advertiser.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_registered && _request is not null)
        {
            // Deregistration completes asynchronously and Windows may still read the request and the
            // instance until its callback runs, so both are intentionally kept alive (a few hundred
            // bytes per listener restart) instead of risking a use-after-free.
            var status = DnsServiceDeRegister(_request, null);
            if (status != DnsRequestPending)
            {
                LogFailed(_logger, $"DnsServiceDeRegister returned {status}");
            }

            _registered = false;
            _request = null;
            _instance = 0;
            return;
        }

        // Never registered: nothing references these, free them now.
        if (_instance != 0)
        {
            DnsServiceFreeInstance(_instance);
            _instance = 0;
        }

        if (_request is not null)
        {
            NativeMemory.Free(_request);
            _request = null;
        }
    }

    private void Register(string displayName, Guid serverId, IPAddress address, int interfaceIndex, int port)
    {
        var host = Environment.MachineName.ToLowerInvariant() + ".local";
        var instanceName = Sanitize(displayName) + "._csmfd._tcp.local";
        var ip4 = BitConverter.ToUInt32(address.GetAddressBytes()); // network byte order, as IP4_ADDRESS expects

        string[] keys = ["v", "tls", "id", "name"];
        string[] values = ["2", "1", serverId.ToString("D"), Sanitize(displayName)];

        var keyHandles = keys.Select(Marshal.StringToHGlobalUni).ToArray();
        var valueHandles = values.Select(Marshal.StringToHGlobalUni).ToArray();
        try
        {
            fixed (nint* keyPointers = keyHandles)
            fixed (nint* valuePointers = valueHandles)
            {
                _instance = DnsServiceConstructInstance(instanceName, host, &ip4, null, (ushort)port, 0, 0, (uint)keys.Length, keyPointers, valuePointers);
            }
        }
        finally
        {
            foreach (var handle in keyHandles.Concat(valueHandles))
            {
                Marshal.FreeHGlobal(handle);
            }
        }

        if (_instance == 0)
        {
            throw new InvalidOperationException("DnsServiceConstructInstance failed");
        }

        _request = (DnsServiceRegisterRequest*)NativeMemory.AllocZeroed((nuint)sizeof(DnsServiceRegisterRequest));
        _request->Version = DnsQueryRequestVersion1;
        _request->InterfaceIndex = (uint)Math.Max(0, interfaceIndex);
        _request->ServiceInstance = _instance;
        _request->RegisterCompletionCallback = &OnRegisterComplete;

        var status = DnsServiceRegister(_request, null);
        if (status != DnsRequestPending)
        {
            throw new InvalidOperationException($"DnsServiceRegister returned {status}");
        }

        _registered = true;
        LogRegistered(_logger, instanceName);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnRegisterComplete(uint status, nint context, nint instance)
    {
        // Windows hands back a copy of the instance that the callee must free.
        if (instance != 0)
        {
            DnsServiceFreeInstance(instance);
        }
    }

    /// <summary>DNS-SD instance labels: keep it short and free of dots.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or ' ' or '_').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "CSMFD";
        }

        return cleaned.Length > 48 ? cleaned[..48] : cleaned;
    }

    [LibraryImport("dnsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint DnsServiceConstructInstance(
        string serviceName,
        string hostName,
        uint* ip4,
        void* ip6,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertiesCount,
        nint* keys,
        nint* values);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceRegister(DnsServiceRegisterRequest* request, void* cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial uint DnsServiceDeRegister(DnsServiceRegisterRequest* request, void* cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsServiceFreeInstance(nint instance);

    [LoggerMessage(Level = LogLevel.Information, Message = "mDNS service registered as {InstanceName}")]
    private static partial void LogRegistered(ILogger logger, string instanceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mDNS registration failed ({Reason}); manual IP entry still works")]
    private static partial void LogFailed(ILogger logger, string reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint ServiceInstance;
        public delegate* unmanaged[Stdcall]<uint, nint, nint, void> RegisterCompletionCallback;
        public nint QueryContext;
        public nint Credentials;
        public int UnicastEnabled;
    }
}
