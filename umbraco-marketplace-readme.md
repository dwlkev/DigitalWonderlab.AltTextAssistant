# Alt Text Assistant

An Umbraco backoffice package that adds an **Alt Text Assistant** dashboard to the Media section. It surfaces every image with no alt text, lets editors fix it inline, and — when an AI provider is configured — suggests alt text straight from the image, one at a time or in bulk.

![Dashboard](https://raw.githubusercontent.com/dwlkev/DigitalWonderlab.AltTextAssistant/main/screenshots/alt-text-assistant-dashboard.png)

![AI suggestion](https://raw.githubusercontent.com/dwlkev/DigitalWonderlab.AltTextAssistant/main/screenshots/alt-text-assistant-suggest.png)

## Features
- Media section dashboard listing all images with an empty alt text, newest first, paged
- Inline editor with image preview to add and save alt text without leaving the dashboard
- One-click **Suggest with AI** — a concise, screen-reader- and SEO-friendly description generated from the image
- **Bulk suggest** across the current page (throttled, cancellable), review, then Save All
- Bring your own key: Anthropic Claude, OpenAI, or Google Gemini vision
- Works fully for manual entry with no key configured — AI controls simply hide

## Compatibility
- Umbraco 17+
- .NET 10.0
- Works on Umbraco Cloud, Azure App Service, and all standard hosting environments

## Installation
- Install via NuGet: https://www.nuget.org/packages/DigitalWonderlab.AltTextAssistant/latest
- Restart the site and open **Media → Alt Text Assistant**
- To enable AI, add a provider API key under a `AltTextAssistant` section in `appsettings.json` (Anthropic → OpenAI → Google priority). See the GitHub README for the config block.

## Privacy
When you use AI suggestions, the image is sent to the provider you configured, using your own API key under your own account and their terms. Images are only sent when you click Suggest, and never if no key is configured. Separately, the package sends us a small anonymous daily usage report with no images, alt text or keys in it (see Telemetry).

## Telemetry
Once a day Alt Text Assistant sends us a small anonymous report so we know how many sites use it and
on which versions. It is on by default and easy to turn off.

- **Sent:** a random install ID (created on first run, stored in your Umbraco database, not derived from anything
  about your site), the package, Umbraco and .NET versions, environment type, hosting type, database type, the image
  library's rough size in bands such as `100-999`, which AI provider is configured (never the key), and how many
  suggestions and saves were made in the last day (banded).
- **Never sent:** API keys, images, alt text, file or media names, hostnames, URLs, user names, emails or IP addresses.
- **See it / turn it off:** the dashboard's **Settings** tab shows exactly what is sent and has an on/off switch. Or set
  `"AltTextAssistant": { "Telemetry": { "Enabled": false } }`, or `"DigitalWonderlab": { "Telemetry": { "Enabled": false } }`
  for every Digital Wonderlab package.
- Local development (`ASPNETCORE_ENVIRONMENT=Development`) never sends anything. Data is kept for 13 months.

## Security (1.1.0)
Up to 1.0.0 any signed-in backoffice user could list every image and change any image's alt text, even without access
to the Media section or outside their media start nodes. 1.1.0 requires the Media section and checks each item
against the user's media start nodes. Release builds also no longer embed the build machine's file path in the DLL.

## Issues / Suggestions
- Please use the GitHub issue tracker: https://github.com/dwlkev/DigitalWonderlab.AltTextAssistant/issues
