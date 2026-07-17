using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.StreamLimit;

/// <summary>
/// Pure helpers to read and write the per-user stream limit map stored in the
/// plugin configuration as a JSON string.
/// </summary>
/// <remarks>
/// Parsing is entry-by-entry on purpose: legacy config pages wrote numbers as
/// strings ("2") and empty strings when a field was cleared, and an all-or-nothing
/// dictionary parse turned one bad entry into "no limits at all".
/// </remarks>
public static class StreamLimitStore
{
    /// <summary>
    /// Normalizes a user id to the storage key format: GUID without dashes, lower case.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The normalized key.</returns>
    public static string NormalizeUserKey(Guid userId) => userId.ToString("N");

    /// <summary>
    /// Parses the serialized limit map. Invalid entries are skipped, an invalid
    /// document yields an empty map. Keys are normalized so both dashed and
    /// dashless user ids are accepted.
    /// </summary>
    /// <param name="json">The serialized map, may be null or empty.</param>
    /// <returns>A map of normalized user id to allowed stream count.</returns>
    public static Dictionary<string, int> ParseLimits(string? json)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        Dictionary<string, JsonElement>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (raw is null)
        {
            return result;
        }

        foreach (var (key, value) in raw)
        {
            if (!Guid.TryParse(key, out var userId))
            {
                continue;
            }

            int limit;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                limit = number;
            }
            else if (value.ValueKind == JsonValueKind.String
                     && int.TryParse(value.GetString(), out var parsed))
            {
                limit = parsed;
            }
            else
            {
                continue;
            }

            if (limit > 0)
            {
                result[NormalizeUserKey(userId)] = limit;
            }
        }

        return result;
    }

    /// <summary>
    /// Serializes a limit map back to its storage format.
    /// </summary>
    /// <param name="limits">The limit map.</param>
    /// <returns>The serialized JSON object.</returns>
    public static string SerializeLimits(IReadOnlyDictionary<string, int> limits)
        => JsonSerializer.Serialize(limits);

    /// <summary>
    /// Resolves the effective limit for a user: the explicit per-user limit when
    /// present, otherwise the default limit. Values of 0 or less mean unlimited.
    /// </summary>
    /// <param name="limits">The parsed limit map.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="defaultLimit">The default limit for users without an explicit entry.</param>
    /// <returns>The effective limit, 0 when unlimited.</returns>
    public static int GetEffectiveLimit(IReadOnlyDictionary<string, int> limits, Guid userId, int defaultLimit)
    {
        if (limits.TryGetValue(NormalizeUserKey(userId), out var limit) && limit > 0)
        {
            return limit;
        }

        return defaultLimit > 0 ? defaultLimit : 0;
    }
}
