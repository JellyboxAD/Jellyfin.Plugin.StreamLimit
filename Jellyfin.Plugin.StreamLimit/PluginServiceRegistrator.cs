using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.StreamLimit.Gate;
using Jellyfin.Plugin.StreamLimit.Notifications;
using Jellyfin.Plugin.StreamLimit.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Session;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// Register StreamLimit services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StreamLimitManager>();
        serviceCollection.AddSingleton<StreamSlotTracker>();
        serviceCollection.AddSingleton<EnforcedDeviceRegistry>();
        serviceCollection.AddSingleton<PlaySessionRegistry>();
        serviceCollection.AddSingleton<StreamEnforcer>();
        serviceCollection.AddSingleton<BlockActivityLogger>();
        serviceCollection.AddSingleton<WebhookNotifier>();
        serviceCollection.AddSingleton<StreamGateFilter>();

        // Hard block: hook the gate into MVC as a global resource filter. Jellyfin
        // configures MvcOptions with a plain Configure delegate and never locks it,
        // so a PostConfigure from plugin DI reliably runs after it.
        serviceCollection.PostConfigure<MvcOptions>(options => options.Filters.AddService<StreamGateFilter>());

        // Reactive enforcement (stop command + transcode kill + optional logout)
        // stays as the safety net behind the HTTP gate.
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackStartLimiter>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackStopSlotReleaser>();
        serviceCollection.AddScoped<IEventConsumer<SessionEndedEventArgs>, SessionEndedSlotReleaser>();

        // Deleted users must not leave limit entries behind in the configuration.
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedLimitRemover>();

        // Adds the custom-message script to the web client as index.html is served
        // (nothing written to disk, so read-only web folders work too)...
        serviceCollection.AddSingleton<IStartupFilter, WebInjectionStartupFilter>();

        // ...and removes the tag older versions wrote into index.html on disk.
        serviceCollection.AddHostedService<WebInjectionService>();
    }
}
