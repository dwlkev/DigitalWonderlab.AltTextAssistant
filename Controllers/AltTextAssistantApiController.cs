using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using DigitalWonderlab.AltTextAssistant.Services;
using DigitalWonderlab.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace DigitalWonderlab.AltTextAssistant.Controllers;

// SECURITY (1.1.0): up to 1.0.0 any signed-in backoffice user could list every image and change any image's
// alt text, regardless of Media section access or media start nodes. Now the Media section is required and
// each item is checked against the user's media start nodes.
[VersionedApiBackOfficeRoute("alt-text-assistant")]
[ApiExplorerSettings(GroupName = "Alt Text Assistant")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessMedia)]
public class AltTextAssistantApiController : ManagementApiControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaTypeService _mediaTypeService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AltTextAssistantApiController> _logger;
    private readonly IBackOfficeSecurityAccessor _security;
    private readonly IEntityService _entityService;
    private readonly AppCaches _appCaches;

    // The default alt-text property alias on Umbraco's Image media type on DWL/baseline
    // sites. Not present on a stock Umbraco install, so it is overridable in config.
    private const string DefaultAltTextAlias = "umbracoAltText";

    private const string AltTextPrompt =
        "Write a concise, descriptive alt text for this image suitable for a website. " +
        "The alt text should be 1-2 sentences (under 125 characters where possible) that describe what is " +
        "shown in the image for screen reader users and SEO. Do not start with \"Image of\" or \"Picture of\". " +
        "If the image is a logo, return only the organisation or brand name followed by the word logo, " +
        "for example \"Northfield Trust logo\". Return only the alt text, nothing else.";

    /// <summary>The prompt for one image: names it, and when improving, shows the alt text being replaced.</summary>
    internal static string BuildPrompt(string? mediaName, string? currentAlt)
    {
        var prompt = AltTextPrompt;
        if (!string.IsNullOrWhiteSpace(mediaName))
            prompt += $"\n\nThe image is named \"{mediaName.Trim()}\" in the media library. Use the name only if it " +
                      "helps identify a brand, person or place; ignore it if it looks like a file or camera name.";
        if (!string.IsNullOrWhiteSpace(currentAlt))
            prompt += $"\n\nIts current alt text is \"{currentAlt.Trim()}\", which has been flagged as not descriptive " +
                      "enough. Write a better replacement. Keep any correct names from it.";
        return prompt;
    }

    public AltTextAssistantApiController(
        IMediaService mediaService,
        IMediaTypeService mediaTypeService,
        MediaFileManager mediaFileManager,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AltTextAssistantApiController> logger,
        IBackOfficeSecurityAccessor security,
        IEntityService entityService,
        AppCaches appCaches)
    {
        _security = security;
        _entityService = entityService;
        _appCaches = appCaches;
        _mediaService = mediaService;
        _mediaTypeService = mediaTypeService;
        _mediaFileManager = mediaFileManager;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>True when the current backoffice user's media start nodes include this item.</summary>
    private bool CanAccess(IMedia media)
    {
        var user = _security.BackOfficeSecurity?.CurrentUser;
        if (user == null) return false;
        var startIds = user.CalculateMediaStartNodeIds(_entityService, _appCaches);
        return ContentPermissions.HasPathAccess(media.Path, startIds, Constants.System.RecycleBinMedia);
    }

    // The media property alias that holds alt text. Configurable so the package
    // works on any site regardless of how the Image media type is set up.
    private string AltTextAlias
    {
        get
        {
            var configured = _configuration["AltTextAssistant:AltTextPropertyAlias"];
            return string.IsNullOrWhiteSpace(configured) ? DefaultAltTextAlias : configured.Trim();
        }
    }

    [HttpGet("images")]
    public IActionResult GetImages(int page = 1, int pageSize = 25)
    {
        var missingAlt = GetMissingAltImages();
        if (missingAlt == null)
            return NotFound(new { error = "Image media type not found." });

        var totalFiltered = missingAlt.Count;
        var totalPages = (int)Math.Ceiling((double)totalFiltered / pageSize);
        var paged = missingAlt.Skip((page - 1) * pageSize).Take(pageSize);

        var result = paged.Select(img =>
        {
            var filePropValue = img.GetValue<string>("umbracoFile");
            var src = ParseImageSrc(filePropValue);

            return new
            {
                id = img.Id,
                key = img.Key,
                name = img.Name,
                createDate = img.CreateDate.ToString("yyyy-MM-dd"),
                src
            };
        });

        return Ok(new
        {
            items = result,
            totalItems = totalFiltered,
            page,
            pageSize,
            totalPages
        });
    }

    /// <summary>Every image missing alt text (not just one page), for "Suggest + save all".</summary>
    [HttpGet("images/all")]
    public IActionResult GetAllImages()
    {
        var missingAlt = GetMissingAltImages();
        if (missingAlt == null)
            return NotFound(new { error = "Image media type not found." });

        return Ok(missingAlt.Select(img => new { id = img.Id, name = img.Name }));
    }

    /// <summary>Images whose alt text exists but does not describe the image, worst first, with the reasons.</summary>
    [HttpGet("images/weak")]
    public IActionResult GetWeakImages(int page = 1, int pageSize = 25)
    {
        var images = GetAccessibleImages();
        if (images == null)
            return NotFound(new { error = "Image media type not found." });

        var alias = AltTextAlias;
        var withAlt = images
            .Select(img => (img, alt: img.GetValue<string>(alias)?.Trim() ?? ""))
            .Where(x => x.alt.Length > 0)
            .ToList();

        // How many images share each alt text, for the "same alt on N images" rule.
        var usage = withAlt
            .GroupBy(x => AltTextQuality.Normalise(x.alt))
            .ToDictionary(g => g.Key, g => g.Count());

        var weak = withAlt
            .Select(x =>
            {
                var src = ParseImageSrc(x.img.GetValue<string>("umbracoFile"));
                var fileName = src.Split('?')[0].Split('/').LastOrDefault();
                var reasons = AltTextQuality.Problems(x.alt, x.img.Name, fileName, usage[AltTextQuality.Normalise(x.alt)]);
                return (x.img, x.alt, src, reasons);
            })
            .Where(x => x.reasons.Count > 0)
            .OrderByDescending(x => x.reasons.Count)
            .ThenByDescending(x => x.img.CreateDate)
            .ToList();

        var totalPages = (int)Math.Ceiling((double)weak.Count / pageSize);
        var items = weak.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new
        {
            id = x.img.Id,
            key = x.img.Key,
            name = x.img.Name,
            createDate = x.img.CreateDate.ToString("yyyy-MM-dd"),
            src = x.src,
            altText = x.alt,
            reasons = x.reasons
        });

        return Ok(new { items, totalItems = weak.Count, page, pageSize, totalPages });
    }

    /// <summary>Images the current user can access with no alt text, newest first. Null if there is no Image type.</summary>
    private List<IMedia>? GetMissingAltImages()
    {
        var alias = AltTextAlias;
        var missingAlt = GetAccessibleImages()?
            .Where(item => string.IsNullOrWhiteSpace(item.GetValue<string>(alias)))
            .ToList();

        missingAlt?.Sort((a, b) => b.CreateDate.CompareTo(a.CreateDate));
        return missingAlt;
    }

    /// <summary>Every Image the current user can access. Null if there is no Image type.</summary>
    private List<IMedia>? GetAccessibleImages()
    {
        var imageType = _mediaTypeService.Get("Image");
        if (imageType == null)
            return null;

        var images = new List<IMedia>();
        var batchIndex = 0;
        const int batchSize = 500;
        long totalMediaCount;

        do
        {
            var batch = _mediaService.GetPagedOfType(
                imageType.Id, batchIndex, batchSize, out totalMediaCount);

            images.AddRange(batch.Where(CanAccess));
            batchIndex++;
        }
        while ((long)batchIndex * batchSize < totalMediaCount);

        return images;
    }

    [HttpPost("save")]
    public IActionResult SaveAltText([FromBody] SaveAltTextRequest request)
    {
        if (request == null || request.MediaId <= 0 || string.IsNullOrWhiteSpace(request.AltText))
            return BadRequest(new { error = "MediaId and AltText are required." });

        var media = _mediaService.GetById(request.MediaId);
        if (media == null)
            return NotFound(new { error = "Media item not found." });
        if (!CanAccess(media))
            return Forbid();

        var alias = AltTextAlias;
        if (!MediaTypeHasAltField(media.ContentType.Alias, alias))
        {
            return BadRequest(new
            {
                error = $"The media type has no '{alias}' property to store alt text. " +
                        "Add an alt text property to the Image media type, or set " +
                        "AltTextAssistant:AltTextPropertyAlias in appsettings.json to the correct alias."
            });
        }

        media.SetValue(alias, request.AltText.Trim());
        _mediaService.Save(media);
        HttpContext.RequestServices.GetService<TelemetryCounters>()?.Increment(AltTextAssistantTelemetryContributor.Saves);

        return Ok(new { success = true });
    }

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var (provider, _) = GetActiveProvider();
        var alias = AltTextAlias;

        return Ok(new
        {
            aiEnabled = provider != null,
            provider = provider ?? "",
            altFieldAlias = alias,
            altFieldExists = MediaTypeHasAltField("Image", alias)
        });
    }

    [HttpPost("suggest")]
    public async Task<IActionResult> SuggestAltText([FromBody] SuggestAltTextRequest request)
    {
        if (request == null || request.MediaId <= 0)
            return BadRequest(new { error = "MediaId is required." });

        var (provider, apiKey) = GetActiveProvider();
        if (provider == null)
            return BadRequest(new { error = "AI is not configured. Add an API key under the AltTextAssistant section of appsettings.json to enable AI-powered alt text suggestions." });

        var media = _mediaService.GetById(request.MediaId);
        if (media == null)
            return NotFound(new { error = "Media item not found." });
        if (!CanAccess(media))
            return Forbid();

        HttpContext.RequestServices.GetService<TelemetryCounters>()?.Increment(AltTextAssistantTelemetryContributor.Suggestions);

        try
        {
            // Read image bytes via MediaFileManager
            byte[] imageBytes;
            var filePath = ParseImageSrc(media.GetValue<string>("umbracoFile"));

            _logger.LogInformation("SuggestAltText: Reading file for media {MediaId}, path: {FilePath}", request.MediaId, filePath);

            using (var stream = _mediaFileManager.GetFile(media, out var resolvedPath))
            {
                _logger.LogInformation("SuggestAltText: Resolved path: {ResolvedPath}, stream null: {IsNull}", resolvedPath, stream == null);

                if (stream == null)
                    return BadRequest(new { error = $"Could not read media file. Path: {filePath}" });

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                imageBytes = ms.ToArray();
            }

            // MIME type from the file's bytes, not its extension (a mislabelled .webp used to be
            // rejected by the provider); oversized or unsupported files are re-encoded.
            AiImagePreparer.PreparedImage prepared;
            try
            {
                prepared = AiImagePreparer.Prepare(imageBytes);
            }
            catch (AiImagePreparer.UnsupportedImageException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            var mimeType = prepared.MimeType;
            _logger.LogInformation("SuggestAltText: Read {Bytes} bytes, sending {SentBytes} bytes as {MimeType} (re-encoded: {Reencoded}) to {Provider}",
                imageBytes.Length, prepared.Bytes.Length, mimeType, prepared.Reencoded, provider);

            var base64 = Convert.ToBase64String(prepared.Bytes);
            var prompt = BuildPrompt(media.Name, request.Improve ? media.GetValue<string>(AltTextAlias) : null);

            // Dispatch to the active provider
            return provider switch
            {
                "Anthropic" => await CallAnthropic(apiKey!, base64, mimeType, prompt),
                "OpenAI" => await CallOpenAI(apiKey!, base64, mimeType, prompt),
                "Google" => await CallGemini(apiKey!, base64, mimeType, prompt),
                _ => BadRequest(new { error = $"Unknown provider: {provider}" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SuggestAltText failed for media {MediaId}", request.MediaId);
            return StatusCode(500, new { error = $"Failed to generate alt text: {ex.Message}" });
        }
    }

    // ──────────────────────────────────────────────
    // Does the given media type (or anything it inherits) expose the alt alias?
    // ──────────────────────────────────────────────
    private bool MediaTypeHasAltField(string mediaTypeAlias, string propertyAlias)
    {
        var mediaType = _mediaTypeService.Get(mediaTypeAlias);
        if (mediaType == null)
            return false;

        return mediaType.CompositionPropertyTypes
            .Any(pt => string.Equals(pt.Alias, propertyAlias, StringComparison.OrdinalIgnoreCase));
    }

    // ──────────────────────────────────────────────
    // Provider resolution — first configured key wins
    // Priority: Anthropic > OpenAI > Google
    // ──────────────────────────────────────────────
    internal (string? provider, string? apiKey) GetActiveProvider() => ActiveProvider(_configuration);

    internal static (string? provider, string? apiKey) ActiveProvider(IConfiguration _configuration)
    {
        var anthropicKey = _configuration["AltTextAssistant:AnthropicApiKey"];
        if (!string.IsNullOrWhiteSpace(anthropicKey))
            return ("Anthropic", anthropicKey);

        var openAiKey = _configuration["AltTextAssistant:OpenAIApiKey"];
        if (!string.IsNullOrWhiteSpace(openAiKey))
            return ("OpenAI", openAiKey);

        var googleKey = _configuration["AltTextAssistant:GoogleApiKey"];
        if (!string.IsNullOrWhiteSpace(googleKey))
            return ("Google", googleKey);

        return (null, null);
    }

    public record TelemetryToggleRequest(bool Enabled);

    [HttpGet("telemetry")]
    public IActionResult GetTelemetry()
    {
        var builder = HttpContext.RequestServices.GetRequiredService<TelemetryPayloadBuilder>();
        return Ok(new { enabled = builder.IsProductToggleOn(AltTextAssistantTelemetryContributor.Product) });
    }

    [HttpPost("telemetry")]
    public IActionResult SaveTelemetry([FromBody] TelemetryToggleRequest request)
    {
        var builder = HttpContext.RequestServices.GetRequiredService<TelemetryPayloadBuilder>();
        builder.SetProductToggle(AltTextAssistantTelemetryContributor.Product, request.Enabled);
        return Ok(new { enabled = request.Enabled });
    }

    /// <summary>The exact heartbeat this site would send next, shown on the dashboard's Settings tab.</summary>
    [HttpGet("telemetry/preview")]
    public IActionResult PreviewTelemetry()
    {
        var builder = HttpContext.RequestServices.GetRequiredService<TelemetryPayloadBuilder>();
        var contributor = HttpContext.RequestServices.GetServices<ITelemetryContributor>()
            .First(c => c.ProductId == AltTextAssistantTelemetryContributor.Product);
        return Ok(new
        {
            endpoint = builder.Endpoint,
            enabled = builder.GloballyEnabled && contributor.IsEnabled(),
            environmentAllowed = builder.EnvironmentAllowed,
            payload = builder.Build(contributor)
        });
    }

    // ──────────────────────────────────────────────
    // Anthropic Claude — Messages API with vision
    // Docs: https://docs.anthropic.com/en/docs/build-with-claude/vision
    // ──────────────────────────────────────────────
    private async Task<IActionResult> CallAnthropic(string apiKey, string base64, string mimeType, string prompt)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var model = _configuration["AltTextAssistant:AnthropicModel"] ?? "claude-haiku-4-5-20251001";

        var payload = new
        {
            model,
            max_tokens = 300,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = mimeType,
                                data = base64
                            }
                        },
                        new { type = "text", text = prompt }
                    }
                }
            }
        };

        var (response, body) = await PostJson(client, "https://api.anthropic.com/v1/messages", payload);
        if (!response.IsSuccessStatusCode)
            return ProviderError(response, body);

        // Response shape: { content: [{ type: "text", text: "..." }] }
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()?.Trim() ?? "";
        return Ok(new { altText = text });
    }

    // ──────────────────────────────────────────────
    // OpenAI — Chat Completions API with vision
    // Docs: https://platform.openai.com/docs/guides/vision
    // ──────────────────────────────────────────────
    private async Task<IActionResult> CallOpenAI(string apiKey, string base64, string mimeType, string prompt)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var model = _configuration["AltTextAssistant:OpenAIModel"] ?? "gpt-4o";

        var payload = new
        {
            model,
            max_tokens = 300,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mimeType};base64,{base64}"
                            }
                        },
                        new { type = "text", text = prompt }
                    }
                }
            }
        };

        var (response, body) = await PostJson(client, "https://api.openai.com/v1/chat/completions", payload);
        if (!response.IsSuccessStatusCode)
            return ProviderError(response, body);

        // Response shape: { choices: [{ message: { content: "..." } }] }
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim() ?? "";
        return Ok(new { altText = text });
    }

    // ──────────────────────────────────────────────
    // Google Gemini — GenerateContent API with inline image
    // Docs: https://ai.google.dev/gemini-api/docs/vision
    // ──────────────────────────────────────────────
    private async Task<IActionResult> CallGemini(string apiKey, string base64, string mimeType, string prompt)
    {
        var client = _httpClientFactory.CreateClient();

        var model = _configuration["AltTextAssistant:GoogleModel"] ?? "gemini-2.0-flash";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = base64
                            }
                        },
                        new { text = prompt }
                    }
                }
            }
        };

        // Gemini uses API key as query parameter
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        var (response, body) = await PostJson(client, url, payload);
        if (!response.IsSuccessStatusCode)
            return ProviderError(response, body);

        // Response shape: { candidates: [{ content: { parts: [{ text: "..." }] } }] }
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString()?.Trim() ?? "";
        return Ok(new { altText = text });
    }

    // ──────────────────────────────────────────────
    // Shared helpers
    // ──────────────────────────────────────────────

    private static async Task<(HttpResponseMessage response, string body)> PostJson(
        HttpClient client, string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        const int maxRetries = 2;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(url, content);
            }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Failed to connect to AI provider: {ex.Message}", ex);
            }

            var body = await response.Content.ReadAsStringAsync();

            // Retry on 500/503/529 (overloaded) with exponential backoff
            var code = (int)response.StatusCode;
            if ((code == 500 || code == 503 || code == 529) && attempt < maxRetries)
            {
                await Task.Delay((attempt + 1) * 1000);
                continue;
            }

            return (response, body);
        }

        // Should never reach here, but just in case
        throw new HttpRequestException("AI provider request failed after retries.");
    }

    private IActionResult ProviderError(HttpResponseMessage response, string body)
    {
        _logger.LogWarning("AI provider returned {StatusCode}: {Body}", (int)response.StatusCode, body);

        var friendlyError = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "The API key is invalid. Check the key configured under the AltTextAssistant section of appsettings.json.",
            System.Net.HttpStatusCode.Forbidden =>
                "The API key does not have permission to use this model. Check your provider account and model access.",
            System.Net.HttpStatusCode.TooManyRequests =>
                "The AI service is temporarily unavailable due to rate limiting or insufficient credits. Please try again later.",
            System.Net.HttpStatusCode.BadRequest =>
                "The image could not be processed by the AI service. It may be too large or in an unsupported format.",
            System.Net.HttpStatusCode.InternalServerError =>
                "This is likely a temporary issue with the AI provider. Please try again.",
            System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout =>
                "The AI service is temporarily unavailable. Please try again in a moment.",
            _ =>
                $"The AI service returned an error (HTTP {(int)response.StatusCode}). Please try again later."
        };

        // A 400 says exactly what the provider disliked about the image; pass that on.
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && ProviderMessage(body) is { } detail)
            friendlyError += $" ({detail})";

        // Always return 502 Bad Gateway for upstream AI errors.
        // Returning 401/403 directly causes ASP.NET auth middleware to
        // intercept and swallow the JSON response body.
        return StatusCode(502, new { error = friendlyError });
    }

    /// <summary>The error.message all three providers put in their error body, trimmed.</summary>
    private static string? ProviderMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg)
                && msg.GetString() is { Length: > 0 } text)
                return text.Length > 300 ? text[..300] + "..." : text;
        }
        catch
        {
            // Not JSON
        }
        return null;
    }

    private static string ParseImageSrc(string? umbracoFileValue)
    {
        if (string.IsNullOrWhiteSpace(umbracoFileValue))
            return string.Empty;

        if (umbracoFileValue.TrimStart().StartsWith("{"))
        {
            try
            {
                var doc = JsonDocument.Parse(umbracoFileValue);
                if (doc.RootElement.TryGetProperty("src", out var srcProp))
                    return srcProp.GetString() ?? string.Empty;
            }
            catch
            {
                // Fall through to return raw value
            }
        }

        return umbracoFileValue;
    }

    public class SaveAltTextRequest
    {
        public int MediaId { get; set; }
        public string AltText { get; set; } = string.Empty;
    }

    public class SuggestAltTextRequest
    {
        public int MediaId { get; set; }

        /// <summary>Rewrite the image's existing alt text rather than writing it from scratch.</summary>
        public bool Improve { get; set; }
    }
}
