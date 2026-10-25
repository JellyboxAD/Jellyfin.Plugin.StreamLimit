using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// Removes a deleted user's explicit stream limit, so the configuration does not keep
/// entries for users that no longer exist.
/// </summary>
public sealed class UserDeletedLimitRemover : IEventConsumer<UserDeletedEventArgs>
{
    private readonly StreamLimitManager _limitManager;
    private readonly ILogger<UserDeletedLimitRemover> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDeletedLimitRemover"/> class.
    /// </summary>
    /// <param name="limitManager">The stream limit manager.</param>
    /// <param name="logger">The logger.</param>
    public UserDeletedLimitRemover(StreamLimitManager limitManager, ILogger<UserDeletedLimitRemover> logger)
    {
        _limitManager = limitManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        var user = eventArgs.Argument;
        if (user is null || _limitManager.GetExplicitLimit(user.Id) is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            // 0 removes the entry (same path as the config page and the API).
            _limitManager.SetLimit(user.Id, 0);
            _logger.LogInformation("Removed the stream limit of deleted user {UserName} ({UserId})", user.Username, user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the stream limit of deleted user {UserId}", user.Id);
        }

        return Task.CompletedTask;
    }
}
