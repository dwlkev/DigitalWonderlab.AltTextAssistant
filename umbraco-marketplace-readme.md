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
When you use AI suggestions, the image is sent to the provider you configured, using your own API key under your own account and their terms. Nothing is sent unless you click Suggest, and nothing is sent if no key is configured.

## Issues / Suggestions
- Please use the GitHub issue tracker: https://github.com/dwlkev/DigitalWonderlab.AltTextAssistant/issues
