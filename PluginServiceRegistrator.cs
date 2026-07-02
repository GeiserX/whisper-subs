using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace WhisperSubs
{
    /// <summary>Contributes WhisperSubs services to Jellyfin's shared DI container.</summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHostedService<Web.FileTransformationRegistrationService>();
        }
    }
}
