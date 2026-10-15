using JellyMusicDiscovery.Services;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyMusicDiscovery.Controllers;

[ApiController]
[Route("mdiscover/playlists")]
[Authorize]
public sealed class PlaylistImportController : ControllerBase
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;
    private readonly PlaylistImportService _imports;
    private readonly IAuthorizationContext _authorization;

    public PlaylistImportController(PlaylistImportService imports, IAuthorizationContext authorization)
    {
        _imports = imports;
        _authorization = authorization;
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users()
        => await IsAdminAsync().ConfigureAwait(false) ? Ok(_imports.Users()) : Forbid();

    [HttpPost("preview")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Preview(IFormFile file)
    {
        if (!await IsAdminAsync().ConfigureAwait(false)) return Forbid();
        var data = await ReadUploadAsync(file, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(_imports.Preview(file.FileName, data));
    }

    [HttpPost("import")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Import(IFormFile file, [FromForm] Guid userId,
        [FromForm] string? playlistName, [FromForm] bool requestMissingAlbums = true)
    {
        if (!await IsAdminAsync().ConfigureAwait(false)) return Forbid();
        var data = await ReadUploadAsync(file, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(await _imports.ImportAsync(file.FileName, data, userId, playlistName,
            requestMissingAlbums, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile()
    {
        if (!await IsAdminAsync().ConfigureAwait(false)) return Forbid();
        return Ok(await _imports.ReconcileAsync(HttpContext.RequestAborted).ConfigureAwait(false));
    }

    private async Task<bool> IsAdminAsync()
    {
        if (User.IsInRole("Administrator")) return true;
        var auth = await _authorization.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return auth?.User?.HasPermission(PermissionKind.IsAdministrator) == true;
    }

    private static async Task<byte[]> ReadUploadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxUploadBytes) throw new ArgumentException("Upload must be between 1 byte and 5 MiB.");
        await using var input = file.OpenReadStream();
        using var output = new MemoryStream((int)file.Length);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return output.ToArray();
    }
}
