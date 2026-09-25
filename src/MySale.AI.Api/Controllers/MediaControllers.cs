using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using MySale.AI.Api.Security;
using MySale.AI.Application.Services;

namespace MySale.AI.Api.Controllers;

/// <summary>Voice input: audio → text. The text then goes through the normal chat endpoint (no separate logic).</summary>
[ApiController]
[Route("api/ai/voice")]
[Authorize(Policy = Policies.Chat)]
public sealed class VoiceController : ControllerBase
{
    private const long MaxAudioBytes = 25L * 1024 * 1024;
    private readonly VoiceService _voice;

    public VoiceController(VoiceService voice) => _voice = voice;

    [HttpGet("status")]
    public async Task<ActionResult<VoiceStatusDto>> Status(CancellationToken ct) => Ok(await _voice.StatusAsync(ct));

    [HttpPost("transcribe")]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(MaxAudioBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAudioBytes + 64 * 1024)]
    public async Task<ActionResult<TranscribeResponse>> Transcribe([FromForm] IFormFile? file, [FromForm, StringLength(20)] string? language, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid request", Detail = "No audio was received." });
        await using var stream = file.OpenReadStream();
        return Ok(await _voice.TranscribeAsync(stream, file.FileName, file.ContentType, file.Length, language, ct));
    }
}

/// <summary>Attachments are uploaded first, then referenced by id in the chat request.</summary>
[ApiController]
[Route("api/ai/attachments")]
[Authorize(Policy = Policies.Chat)]
public sealed class AttachmentsController : ControllerBase
{
    /// <summary>Hard transport cap; the configurable limit (Settings → Attachments) is enforced by the service.</summary>
    private const long MaxUploadBytes = 50L * 1024 * 1024;
    private readonly AttachmentService _attachments;

    public AttachmentsController(AttachmentService attachments) => _attachments = attachments;

    [HttpPost]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(MaxUploadBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes + 64 * 1024)]
    public async Task<ActionResult<AttachmentDto>> Upload([FromForm] IFormFile? file, [FromForm, StringLength(64)] string? conversationId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid request", Detail = "No file was received." });
        await using var stream = file.OpenReadStream();
        var dto = await _attachments.UploadAsync(stream, file.FileName, file.ContentType, file.Length, conversationId, allowAudio: false, ct);
        return Ok(dto);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AttachmentDto>> Get(string id, CancellationToken ct) => Ok(await _attachments.GetAsync(id, ct));

    /// <summary>
    /// Returns the stored bytes for previews. Always served as a download-safe response (nosniff, sandbox CSP),
    /// and only images/PDF render inline — never HTML or scripts.
    /// </summary>
    [HttpGet("{id}/content")]
    public async Task<IActionResult> GetContent(string id, CancellationToken ct)
    {
        var (content, contentType, fileName) = await _attachments.OpenAsync(id, ct);
        var inline = contentType.StartsWith("image/", StringComparison.Ordinal) || contentType == "application/pdf";
        Response.Headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'";
        Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        return File(content, inline ? contentType : "application/octet-stream");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _attachments.DeleteAsync(id, ct);
        return NoContent();
    }
}

/// <summary>What the selected provider/model supports (vision, audio…), so the UI never fails silently.</summary>
[ApiController]
[Route("api/ai/capabilities")]
[Authorize(Policy = Policies.Chat)]
public sealed class CapabilitiesController : ControllerBase
{
    private readonly ModelCapabilityService _capabilities;

    public CapabilitiesController(ModelCapabilityService capabilities) => _capabilities = capabilities;

    [HttpGet]
    public async Task<ActionResult<CapabilitiesDto>> Get([FromQuery, StringLength(64)] string? providerId, [FromQuery, StringLength(200)] string? model, CancellationToken ct)
        => Ok(await _capabilities.DescribeAsync(providerId, model, ct));
}
