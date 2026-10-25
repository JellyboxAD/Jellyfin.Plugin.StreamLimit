using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.StreamLimit.Gate;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// What is known about a blocked stream when it is reported to the activity log.
/// </summary>
/// <param name="UserId">The blocked user.</param>
/// <param name="UserName">The user's name, when already known.</param>
/// <param name="DeviceId">The device id.</param>
/// <param name="DeviceName">The device name, when known.</param>
/// <param name="Client">The client application, when known.</param>
/// <param name="RemoteAddress">The client's IP address, when known.</param>
/// <param name="Limit">The user's effective limit.</param>
/// <param name="BlockedBy">Which enforcement layer blocked the stream.</param>
/// <param name="Active">The user's other streams counted by the plugin.</param>
/// <param name="OtherDevices">Names of the user's other playing devices, comma-separated.</param>
public sealed record BlockedStream(
    Guid UserId,
    string? UserName,
    string DeviceId,
    string? DeviceName,
    string? Client,
    string? RemoteAddress,
    int Limit,
    string BlockedBy,
    int Active = 0,
    string? OtherDevices = null)
{
    /// <summary>
    /// Gets the values of the blocked-message placeholders for this block.
    /// </summary>
    public MessageValues MessageValues => new(UserName, Active, Limit, OtherDevices);
}

/// <summary>
/// Writes an entry to the server's activity log (Dashboard &gt; Activity) when a stream
/// is blocked, so admins can see who hit their limit, where and from which address.
/// </summary>
/// <remarks>
/// One blocked playback attempt produces a burst of denials (PlaybackInfo, playlists,
/// segments, client retries), so entries are throttled to one per user and device every
/// <see cref="ThrottleInterval"/>. Writing is detached and best effort: a failing
/// activity log never delays or breaks enforcement.
/// </remarks>
public sealed class BlockActivityLogger
{
    /// <summary>
    /// The activity entry type.
    /// </summary>
    public const string ActivityType = "StreamLimitBlocked";

    /// <summary>
    /// The activity entry type of a stream stopped by an administrator.
    /// </summary>
    public const string AdminKickActivityType = "StreamLimitAdminStop";

    /// <summary>
    /// Minimum time between two entries for the same user and device.
    /// </summary>
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromMinutes(5);

    private readonly IActivityManager _activityManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<BlockActivityLogger> _logger;
    private readonly KeyedThrottle _throttle = new(ThrottleInterval);

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockActivityLogger"/> class.
    /// </summary>
    /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public BlockActivityLogger(IActivityManager activityManager, IUserManager userManager, ILogger<BlockActivityLogger> logger)
    {
        _activityManager = activityManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Records a blocked stream in the activity log, unless disabled or throttled.
    /// </summary>
    /// <param name="blocked">The blocked stream.</param>
    public void LogBlocked(BlockedStream blocked)
    {
        if (Plugin.Instance?.Configuration.LogBlocksToActivity != true
            || !_throttle.TryAcquire(KeyedThrottle.UserDeviceKey(blocked.UserId, blocked.DeviceId)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var userName = blocked.UserName;
                if (string.IsNullOrEmpty(userName))
                {
                    userName = _userManager.GetUserById(blocked.UserId)?.Username;
                }

                var (name, shortOverview, overview) = Describe(blocked with { UserName = userName });
                await _activityManager.CreateAsync(new ActivityLog(name, ActivityType, blocked.UserId)
                {
                    ShortOverview = shortOverview,
                    Overview = overview,
                    LogSeverity = LogLevel.Information,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not write the stream limit block to the activity log");
            }
        });
    }

    /// <summary>
    /// Records that an administrator stopped a stream from the live view. Not throttled
    /// and not subject to <see cref="Configuration.PluginConfiguration.LogBlocksToActivity"/>:
    /// it is an audit entry of an admin action.
    /// </summary>
    /// <param name="adminId">The administrator.</param>
    /// <param name="adminName">The administrator's name.</param>
    /// <param name="stopped">The stopped stream.</param>
    /// <param name="loggedOut">Whether the device was also logged out.</param>
    public void LogAdminKick(Guid adminId, string? adminName, BlockedStream stopped, bool loggedOut)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var (name, shortOverview, overview) = DescribeAdminKick(adminName, stopped, loggedOut);
                await _activityManager.CreateAsync(new ActivityLog(name, AdminKickActivityType, adminId)
                {
                    ShortOverview = shortOverview,
                    Overview = overview,
                    LogSeverity = LogLevel.Information,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not write the admin stream stop to the activity log");
            }
        });
    }

    /// <summary>
    /// Builds the activity entry texts of a stream stopped by an administrator.
    /// </summary>
    /// <param name="adminName">The administrator's name.</param>
    /// <param name="stopped">The stopped stream.</param>
    /// <param name="loggedOut">Whether the device was also logged out.</param>
    /// <returns>The entry name, short overview and full overview.</returns>
    public static (string Name, string ShortOverview, string Overview) DescribeAdminKick(string? adminName, BlockedStream stopped, bool loggedOut)
    {
        var admin = Or(adminName, "An administrator");
        var user = Or(stopped.UserName, StreamLimitStore.NormalizeUserKey(stopped.UserId));
        var device = Or(stopped.DeviceName, stopped.DeviceId);

        var name = string.Format(CultureInfo.InvariantCulture, "Admin {0} stopped the stream of {1} on {2}", admin, user, device);
        var shortOverview = loggedOut ? "Stopped from Stream Limiter, device logged out" : "Stopped from Stream Limiter";
        var overview = new StringBuilder()
            .Append("Administrator: ").Append(admin).Append('\n')
            .Append("User: ").Append(user).Append('\n')
            .Append("Device: ").Append(Or(stopped.DeviceName, "unknown")).Append(" (id ").Append(stopped.DeviceId).Append(")\n")
            .Append("Client: ").Append(Or(stopped.Client, "unknown")).Append('\n')
            .Append("IP address: ").Append(Or(stopped.RemoteAddress, "unknown")).Append('\n')
            .Append("Logged out: ").Append(loggedOut ? "yes" : "no")
            .ToString();

        return (name, shortOverview, overview);
    }

    /// <summary>
    /// Builds the activity entry texts of a blocked stream.
    /// </summary>
    /// <param name="blocked">The blocked stream.</param>
    /// <returns>The entry name, short overview and full overview.</returns>
    public static (string Name, string ShortOverview, string Overview) Describe(BlockedStream blocked)
    {
        var user = Or(blocked.UserName, StreamLimitStore.NormalizeUserKey(blocked.UserId));
        var device = Or(blocked.DeviceName, blocked.DeviceId);

        var name = string.Format(CultureInfo.InvariantCulture, "{0} was blocked from streaming on {1} (stream limit)", user, device);
        var shortOverview = string.Format(
            CultureInfo.InvariantCulture,
            "Limit of {0} simultaneous stream{1} reached",
            blocked.Limit,
            blocked.Limit == 1 ? string.Empty : "s");

        var overview = new StringBuilder()
            .Append("User: ").Append(user).Append('\n')
            .Append("Device: ").Append(Or(blocked.DeviceName, "unknown")).Append(" (id ").Append(blocked.DeviceId).Append(")\n")
            .Append("Client: ").Append(Or(blocked.Client, "unknown")).Append('\n')
            .Append("IP address: ").Append(Or(blocked.RemoteAddress, "unknown")).Append('\n')
            .Append("Blocked by: ").Append(blocked.BlockedBy)
            .ToString();

        return (name, shortOverview, overview);
    }

    private static string Or(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
