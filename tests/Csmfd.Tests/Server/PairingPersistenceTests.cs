using Csmfd.Core.Security;
using Csmfd.Server.Hosting;
using Csmfd.Server.Security;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Server;

public sealed class PairingPersistenceTests
{
    [Fact]
    public void RestartAndSecondPairingPreserveTheExistingDeviceToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "csmfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = new FakeTimeProvider();
            var first = new PairingService(time, new FileTrustStore(new ServerStorage(directory)));
            var paired = first.TryPair(first.OpenWindow(), "First test iPad");
            Assert.Equal(PairOutcome.Paired, paired.Outcome);
            var restarted = new PairingService(time, new FileTrustStore(new ServerStorage(directory)));
            Assert.Equal(paired.Device!.Id, restarted.Authenticate(paired.Token!)!.Id);
            _ = restarted.TryPair(restarted.OpenWindow(), "Second test iPad");
            var restartedAgain = new PairingService(time, new FileTrustStore(new ServerStorage(directory)));
            Assert.Equal(2, restartedAgain.Devices.Count);
            Assert.Equal(paired.Device.Id, restartedAgain.Authenticate(paired.Token!)!.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
