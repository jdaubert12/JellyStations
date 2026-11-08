using System.Net.Mime;
using Jellyfin.Plugin.JellyStations.Library;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyStations.Api;

/// <summary>The Jellyfin users the Settings page offers for "Our household".</summary>
[ApiController]
[Route("JellyStations/Household")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class HouseholdController : ControllerBase
{
    private readonly IHouseholdHistory _household;

    public HouseholdController(IHouseholdHistory household)
    {
        _household = household;
    }

    /// <summary>Every user on the server, by name.</summary>
    [HttpGet("Users")]
    public ActionResult<IEnumerable<HouseholdUser>> Users() => Ok(_household.Users());
}
