using Csmfd.Core.Security;
using Csmfd.Server.Hosting;

namespace Csmfd.Server.Security;

/// <summary>Trusted devices as JSON; holds only token hashes, never tokens.</summary>
internal sealed class FileTrustStore(ServerStorage storage) : ITrustStore
{
    public IReadOnlyList<TrustedDevice> Load() =>
        ServerStorage.ReadJson<List<TrustedDevice>>(storage.TrustPath) ?? [];

    public void Save(IReadOnlyList<TrustedDevice> devices) =>
        storage.WriteJson(storage.TrustPath, devices);
}
