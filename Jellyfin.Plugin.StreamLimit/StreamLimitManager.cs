using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.StreamLimit.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// Thread-safe access to the configured stream limits. Registered as a singleton;
/// the parsed limit map is cached and invalidated whenever the underlying
/// configuration string changes (config page saves, API calls).
/// </summary>
public class StreamLimitManager
{
    private readonly ILogger<StreamLimitManager> _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new();
    private string? _cachedRaw;
    private Dictionary<string, int> _cachedLimits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public StreamLimitManager(ILogger<StreamLimitManager> logger)
    {
        _logger = logger;
    }

    private static PluginConfiguration? Configuration => Plugin.Instance?.Configuration;

    /// <summary>
    /// Gets the effective stream limit for a user (explicit limit or the configured
    /// default). 0 means unlimited.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The effective limit.</returns>
    public int GetEffectiveLimit(Guid userId)
    {
        var configuration = Configuration;
        if (configuration is null)
        {
            return 0;
        }

        return StreamLimitStore.GetEffectiveLimit(GetLimits(configuration), userId, configuration.DefaultMaxStreams);
    }

    /// <summary>
    /// Gets the explicit limit configured for a user, or null when the user has no entry.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The explicit limit or null.</returns>
    public int? GetExplicitLimit(Guid userId)
    {
        var configuration = Configuration;
        if (configuration is null)
        {
            return null;
        }

        return GetLimits(configuration).TryGetValue(StreamLimitStore.NormalizeUserKey(userId), out var limit)
            ? limit
            : null;
    }

    /// <summary>
    /// Gets a snapshot of all explicit per-user limits keyed by dashless user id.
    /// </summary>
    /// <returns>The limit map snapshot.</returns>
    public IReadOnlyDictionary<string, int> GetAllLimits()
    {
        var configuration = Configuration;
        if (configuration is null)
        {
            return new Dictionary<string, int>();
        }

        lock (_lock)
        {
            return new Dictionary<string, int>(GetLimitsUnsafe(configuration), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Sets the explicit limit for a user and persists the configuration.
    /// A value of 0 or less removes the entry (the default limit applies again).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="maxStreams">The maximum allowed streams.</param>
    public void SetLimit(Guid userId, int maxStreams)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");

        Dictionary<string, int> limits;
        lock (_lock)
        {
            limits = new Dictionary<string, int>(GetLimitsUnsafe(plugin.Configuration), StringComparer.OrdinalIgnoreCase);
            var key = StreamLimitStore.NormalizeUserKey(userId);
            if (maxStreams > 0)
            {
                limits[key] = maxStreams;
            }
            else
            {
                limits.Remove(key);
            }

            plugin.Configuration.UserStreamLimits = StreamLimitStore.SerializeLimits(limits);
            _cachedRaw = plugin.Configuration.UserStreamLimits;
            _cachedLimits = limits;
        }

        // Disk write happens outside the lock: playback-start checks must not
        // stall behind XML serialization.
        plugin.SaveConfiguration();
        _logger.LogInformation("Stream limit for user {UserId} set to {MaxStreams}", userId, maxStreams);
    }

    /// <summary>
    /// Gets a per-user lock used to serialize limit enforcement, so concurrent
    /// playback starts of the same user cannot race the count-then-stop decision.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The user's enforcement lock.</returns>
    public SemaphoreSlim GetUserEnforcementLock(Guid userId)
        => _userLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    private Dictionary<string, int> GetLimits(PluginConfiguration configuration)
    {
        lock (_lock)
        {
            return GetLimitsUnsafe(configuration);
        }
    }

    private Dictionary<string, int> GetLimitsUnsafe(PluginConfiguration configuration)
    {
        var raw = configuration.UserStreamLimits;
        if (!string.Equals(raw, _cachedRaw, StringComparison.Ordinal))
        {
            _cachedLimits = StreamLimitStore.ParseLimits(raw);
            _cachedRaw = raw;
            _logger.LogInformation("Loaded {Count} user stream limit(s)", _cachedLimits.Count);
        }

        return _cachedLimits;
    }
}
