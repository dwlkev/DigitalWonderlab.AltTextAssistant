using DigitalWonderlab.AltTextAssistant.Controllers;
using DigitalWonderlab.Telemetry;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Core.Services;

namespace DigitalWonderlab.AltTextAssistant.Services;

/// <summary>
/// What Alt Text Assistant reports in its anonymous daily heartbeat: the image library's rough size (banded),
/// which AI provider is configured (never the key), and how many suggestions and saves were made in the last
/// day (banded). See README "Telemetry".
/// </summary>
internal sealed class AltTextAssistantTelemetryContributor : ITelemetryContributor
{
    public const string Product = "alt-text-assistant";
    public const string Suggestions = "suggestions";
    public const string Saves = "saves";

    private readonly IMediaTypeService _mediaTypeService;
    private readonly IMediaService _mediaService;
    private readonly TelemetryPayloadBuilder _builder;
    private readonly TelemetryCounters _counters;
    private readonly IConfiguration _configuration;

    public AltTextAssistantTelemetryContributor(IMediaTypeService mediaTypeService, IMediaService mediaService,
        TelemetryPayloadBuilder builder, TelemetryCounters counters, IConfiguration configuration)
    {
        _mediaTypeService = mediaTypeService;
        _mediaService = mediaService;
        _builder = builder;
        _counters = counters;
        _configuration = configuration;
    }

    public string ProductId => Product;

    public string? PackageVersion => TelemetryPayloadBuilder.VersionOf(typeof(AltTextAssistantTelemetryContributor).Assembly);

    public bool IsEnabled()
        => _configuration.GetValue<bool?>("AltTextAssistant:Telemetry:Enabled") != false
           && _builder.IsProductToggleOn(Product);

    public void Contribute(TelemetryReport report)
    {
        var imageType = _mediaTypeService.Get("Image");
        long images = 0;
        if (imageType != null)
            _mediaService.GetPagedOfType(imageType.Id, 0, 1, out images);
        report.SizeBand["images"] = TelemetryBand.Of(images);

        var (provider, _) = AltTextAssistantApiController.ActiveProvider(_configuration);
        report.Features["ai"] = provider != null;
        report.Features["anthropic"] = provider == "Anthropic";
        report.Features["openai"] = provider == "OpenAI";
        report.Features["google"] = provider == "Google";

        _counters.AddTo(report, Suggestions, Saves);
    }
}
