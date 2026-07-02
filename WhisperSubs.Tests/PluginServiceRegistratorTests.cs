using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WhisperSubs.Web;
using Xunit;

namespace WhisperSubs.Tests;

public class PluginServiceRegistratorTests
{
    // Issue #108: the File Transformation registration only happens because this hosted service is
    // wired into Jellyfin's DI container. If this registration is ever dropped, the whole serve-time
    // injection feature silently disappears — so pin it.
    [Fact]
    public void RegisterServices_RegistersFileTransformationHostedService()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(FileTransformationRegistrationService));
    }
}
