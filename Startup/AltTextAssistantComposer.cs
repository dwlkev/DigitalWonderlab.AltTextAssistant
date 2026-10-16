using DigitalWonderlab.AltTextAssistant.Services;
using DigitalWonderlab.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace DigitalWonderlab.AltTextAssistant.Startup;

public class AltTextAssistantComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // The controller resolves IHttpClientFactory to call the configured AI provider.
        builder.Services.AddHttpClient();

        // Anonymous daily telemetry heartbeat (see README Telemetry).
        builder.Services.AddDwlTelemetryProduct<AltTextAssistantTelemetryContributor>();
    }
}
