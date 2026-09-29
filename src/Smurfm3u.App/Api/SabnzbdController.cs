using Microsoft.AspNetCore.Mvc;
using Smurfm3u.App.Services;

namespace Smurfm3u.App.Api;

/// <summary>
/// The SABnzbd-compatible endpoint Sonarr and Radarr point at as their download client.
/// Also reachable at <c>/api</c>, which is where those clients look when their URL base is
/// left empty; see <see cref="NewznabController"/> for how the two share that path.
/// </summary>
[ApiController]
[Route("sabnzbd/api")]
public class SabnzbdController(SabnzbdHandler handler) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await handler.HandleAsync(await ReadRequestAsync(HttpContext), ct));

    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct) =>
        Ok(await handler.HandleAsync(await ReadRequestAsync(HttpContext), ct));

    /// <summary>
    /// Collapses both transports into one shape. Clients send parameters in the query string
    /// for most calls and as multipart form fields when uploading an nzb.
    /// </summary>
    internal static async Task<SabRequest> ReadRequestAsync(HttpContext http)
    {
        var query = http.Request.Query;

        IFormCollection? form = null;
        if (http.Request.HasFormContentType)
            form = await http.Request.ReadFormAsync(http.RequestAborted);

        byte[]? uploaded = null;
        var file = form?.Files.GetFile("nzbfile") ?? form?.Files.FirstOrDefault();
        if (file is { Length: > 0 })
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, http.RequestAborted);
            uploaded = buffer.ToArray();
        }

        return new SabRequest
        {
            Mode = Read("mode") ?? string.Empty,
            Name = Read("name"),
            Value = Read("value"),
            Value2 = Read("value2"),
            Category = Read("cat") ?? Read("category"),
            Priority = int.TryParse(Read("priority"), out var priority) ? priority : null,
            DeleteFiles = Read("del_files") is "1" or "true",
            NzbName = Read("nzbname"),
            UploadedFile = uploaded,
            ApiKey = Read("apikey")
        };

        string? Read(string key)
        {
            if (query.TryGetValue(key, out var fromQuery) && fromQuery.Count > 0)
                return fromQuery.ToString();

            if (form is not null && form.TryGetValue(key, out var fromForm) && fromForm.Count > 0)
                return fromForm.ToString();

            return null;
        }
    }
}
