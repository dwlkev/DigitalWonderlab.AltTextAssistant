using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace DigitalWonderlab.AltTextAssistant.Controllers;

[VersionedApiBackOfficeRoute("alt-text-assistant")]
[ApiExplorerSettings(GroupName = "Alt Text Assistant")]
public class AltTextAssistantApiController : ManagementApiControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaTypeService _mediaTypeService;
    private readonly MediaFileManager _mediaFileManager;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AltTextAssistantApiController> _logger;

    // The default alt-text property alias on Umbraco's Image media type on DWL/baseline
    // sites. Not present on a stock Umbraco install, so it is overridable in config.
    private const string DefaultAltTextAlias = "umbracoAltText";

    private const string AltTextPrompt =
        "Write a concise, descriptive alt text for this image suitable for a website. " +
        "The alt text should be 1-2 sentences that describe what is shown in the image " +
        "for screen reader users and SEO. Return only the alt text, nothing else.";

    public AltTextAssistantApiController(
        IMediaService mediaService,
        IMediaTypeService mediaTypeService,
        MediaFileManager mediaFileManager,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AltTextAssistantApiController> logger)
    {
        _mediaService = mediaService;
        _mediaTypeService = mediaTypeService;
        _mediaFileManager = mediaFileManager;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        var imageType = _mediaTypeService.Get("Image");
        if (imageType == null)
            return NotFound(new { error = "Image media type not found." });

        var alias = AltTextAlias;
        var missingAlt = new List<IMedia>();
        var batchIndex = 0;
        const int batchSize = 500;
        long totalMediaCount;

        do
        {
            var batch = _mediaService.GetPagedOfType(
                imageType.Id, batchIndex, batchSize, out totalMediaCount);

            foreach (var item in batch)
            {
                var altText = item.GetValue<string>(alias);
                if (string.IsNullOrWhiteSpace(altText))
                    missingAlt.Add(item);
            }

            batchIndex++;
        }
        while ((long)batchIndex * batchSize < totalMediaCount);

        missingAlt.Sort((a, b) => b.CreateDate.CompareTo(a.CreateDate));

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

    [HttpPost("save")]
    public IActionResult SaveAltText([FromBody] SaveAltTextRequest request)
    {
        if (request == null || request.MediaId <= 0 || string.IsNullOrWhiteSpace(request.AltText))
            return BadRequest(new { error = "MediaId and AltText are required." });

        var media = _mediaService.GetById(request.MediaId);
        if (media == null)
            return NotFound(new { error = "Media item not found." });

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

            _logger.LogInformation("SuggestAltText: Read {Bytes} bytes, sending to {Provider}", imageBytes.Length, provider);

            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var mimeType = ext switch
            {
                "jpg" or "jpeg" => "image/jpeg",
                "png" => "image/png",
                "gif" => "image/gif",
                "webp" => "image/webp",
                "svg" => "image/svg+xml",
                _ => "image/jpeg"
            };

            var base64 = Convert.ToBase64String(imageBytes);

            // Dispatch to the active provider
            return provider switch
            {
                "Anthropic" => await CallAnthropic(apiKey!, base64, mimeType),
                "OpenAI" => await CallOpenAI(apiKey!, base64, mimeType),
                "Google" => await CallGemini(apiKey!, base64, mimeType),
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
    private (string? provider, string? apiKey) GetActiveProvider()
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

    // ──────────────────────────────────────────────
    // Anthropic Claude — Messages API with vision
    // Docs: https://docs.anthropic.com/en/docs/build-with-claude/vision
    // ──────────────────────────────────────────────
    private async Task<IActionResult> CallAnthropic(string apiKey, string base64, string mimeType)
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
                        new { type = "text", text = AltTextPrompt }
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
    private async Task<IActionResult> CallOpenAI(string apiKey, string base64, string mimeType)
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
                        new { type = "text", text = AltTextPrompt }
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
    private async Task<IActionResult> CallGemini(string apiKey, string base64, string mimeType)
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
                        new { text = AltTextPrompt }
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

        // Always return 502 Bad Gateway for upstream AI errors.
        // Returning 401/403 directly causes ASP.NET auth middleware to
        // intercept and swallow the JSON response body.
        return StatusCode(502, new { error = friendlyError });
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
    }
}
