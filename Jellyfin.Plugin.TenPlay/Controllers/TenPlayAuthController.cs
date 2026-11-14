using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TenPlay.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TenPlay.Controllers;

/// <summary>Provides elevated dashboard actions for the official device-code sign-in flow.</summary>
[ApiController]
[Route("TenPlay/Auth")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class TenPlayAuthController : ControllerBase
{
    /// <summary>Gets the local device-login state without exposing account tokens.</summary>
    [HttpGet("status")]
    public ActionResult<LoginStatus> Status()
    {
        TenPlayPlugin? plugin = TenPlayPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 10 Play plugin is not initialized.", statusCode: 503);
        }

        return Ok(new LoginStatus(!string.IsNullOrWhiteSpace(plugin.Configuration.AccessToken)));
    }

    /// <summary>Creates a 10 Play device code for the server administrator.</summary>
    [HttpPost("start")]
    public async Task<ActionResult<DeviceLoginStart>> Start(CancellationToken cancellationToken)
    {
        TenPlayPlugin? plugin = TenPlayPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 10 Play plugin is not initialized.", statusCode: 503);
        }

        return Ok(await TenPlayApiClient.StartDeviceLoginAsync(plugin.Configuration, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Completes a device login after the user approves it on 10 Play.</summary>
    [HttpPost("complete")]
    public async Task<ActionResult<LoginStatus>> Complete([FromBody] CompleteDeviceLoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TenPlayPlugin? plugin = TenPlayPlugin.Instance;
        if (plugin is null)
        {
            return Problem("The 10 Play plugin is not initialized.", statusCode: 503);
        }

        DeviceLoginCompletion completion = await TenPlayApiClient.CompleteDeviceLoginAsync(plugin.Configuration, request.Code, request.ExpiresAtUnixSeconds, cancellationToken).ConfigureAwait(false);
        if (completion.Complete)
        {
            plugin.SaveConfiguration(plugin.Configuration);
        }

        bool complete = completion.Complete || !string.IsNullOrWhiteSpace(plugin.Configuration.AccessToken);
        return Ok(new LoginStatus(complete, completion.Detail));
    }
}

/// <summary>Payload used to poll a device-code login.</summary>
public sealed record CompleteDeviceLoginRequest(string Code, long ExpiresAtUnixSeconds);

/// <summary>Whether the device-code login has completed.</summary>
public sealed record LoginStatus(bool Complete, string? Detail = null);
