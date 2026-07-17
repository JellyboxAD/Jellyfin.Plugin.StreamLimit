using System;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamLimit.Api;

/// <summary>
/// Stream limit administration endpoints. Restricted to elevated users:
/// otherwise any authenticated user could raise their own limit.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("[controller]/[action]")]
[Produces(MediaTypeNames.Application.Json)]
public class StreamLimitController : ControllerBase
{
    private readonly ILogger<StreamLimitController> _logger;
    private readonly IUserManager _userManager;
    private readonly StreamLimitManager _limitManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="limitManager">The stream limit manager.</param>
    public StreamLimitController(
        ILogger<StreamLimitController> logger,
        IUserManager userManager,
        StreamLimitManager limitManager)
    {
        _logger = logger;
        _userManager = userManager;
        _limitManager = limitManager;
    }

    /// <summary>
    /// Gets the stream limit configured for a user.
    /// </summary>
    /// <param name="userId">The user id (with or without dashes).</param>
    /// <returns>The explicit and effective limit for the user.</returns>
    [HttpGet]
    public IActionResult GetUserStreamLimit([FromQuery] string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid))
        {
            return BadRequest("Invalid user id");
        }

        if (_userManager.GetUserById(userGuid) is null)
        {
            return NotFound("User does not exist");
        }

        return Ok(new
        {
            userId = StreamLimitStore.NormalizeUserKey(userGuid),
            streamsAllowed = _limitManager.GetEffectiveLimit(userGuid),
            explicitLimit = _limitManager.GetExplicitLimit(userGuid),
        });
    }

    /// <summary>
    /// Gets all explicit per-user limits and the default limit.
    /// </summary>
    /// <returns>The limit map.</returns>
    [HttpGet]
    public IActionResult GetAllStreamLimits()
    {
        return Ok(new
        {
            defaultMaxStreams = Plugin.Instance?.Configuration.DefaultMaxStreams ?? 0,
            limits = _limitManager.GetAllLimits(),
        });
    }

    /// <summary>
    /// Sets the stream limit for a user. A value of 0 removes the explicit
    /// limit so the default limit applies again.
    /// </summary>
    /// <param name="userId">The user id (with or without dashes).</param>
    /// <param name="streamsAllowed">The maximum number of simultaneous streams, 0 to remove.</param>
    /// <returns>Status of the operation.</returns>
    [HttpPost]
    public IActionResult SetUserStreamLimit([FromQuery] string userId, [FromQuery] int streamsAllowed)
    {
        if (!Guid.TryParse(userId, out var userGuid))
        {
            return BadRequest("Invalid user id");
        }

        if (streamsAllowed < 0)
        {
            return BadRequest("streamsAllowed must be 0 (unlimited) or a positive number");
        }

        if (_userManager.GetUserById(userGuid) is null)
        {
            return NotFound("User does not exist");
        }

        try
        {
            _limitManager.SetLimit(userGuid, streamsAllowed);
            return Ok(new
            {
                userId = StreamLimitStore.NormalizeUserKey(userGuid),
                streamsAllowed,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set stream limit for user {UserId}", userGuid);
            return StatusCode(500, "Failed to save the stream limit");
        }
    }

    /// <summary>
    /// Sets the title and text of the message shown when a stream is blocked.
    /// </summary>
    /// <param name="alertMessage">The message text.</param>
    /// <param name="title">The message title.</param>
    /// <returns>Status of the operation.</returns>
    [HttpPost]
    public IActionResult SetAlertMessage([FromQuery] string alertMessage, [FromQuery] string title)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, "Plugin instance is not available");
        }

        try
        {
            plugin.Configuration.MessageText = alertMessage ?? string.Empty;
            plugin.Configuration.MessageTitle = title ?? string.Empty;
            plugin.SaveConfiguration();
            return Ok(new { title, message = alertMessage });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert message");
            return StatusCode(500, "Failed to save the alert message");
        }
    }
}
