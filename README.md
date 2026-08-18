# Alt Text Assistant

An Umbraco backoffice package that adds an **Alt Text Assistant** dashboard to the Media section. It lists every image in the media library that has no alt text, lets editors add alt text inline, and — when an AI provider is configured — suggests alt text from the image with a single click, or in bulk across the whole page.

## Features

- **Media section dashboard** listing all images with no alt text, newest first, paged
- **Inline editor** with an image preview to add and save alt text without leaving the dashboard
- **AI suggestions** — one-click "Suggest with AI" reads the image and returns a concise, screen-reader- and SEO-friendly description
- **Bulk suggest** — generate suggestions for every image on the current page (throttled, cancellable), review, then Save All
- **Bring your own key** — supports Anthropic Claude, OpenAI, or Google Gemini vision models. First configured key wins
- **Graceful without AI** — with no key configured the dashboard still works for manual alt text; AI controls are hidden
- **Works with any Image media type** — the alt-text property alias is configurable, and the dashboard warns clearly if no alt-text field exists
- **Edit in Media** shortcut to jump to the full media item
- Missing-count badge so you can see progress at a glance

## Compatibility

- Umbraco 17+
- .NET 10.0
- Works on Umbraco Cloud, Azure App Service, and all standard hosting environments
- No server-side dependencies beyond the CMS — image bytes are read via Umbraco's `MediaFileManager` and sent directly to your chosen AI provider over HTTPS

## Requirements — an alt-text field on the Image media type

The package reads and writes an alt-text property on the **Image** media type. By default it uses the alias **`umbracoAltText`**.

A stock Umbraco install does **not** include an alt-text property on the Image media type. If yours doesn't, either:

- Add a textstring property (e.g. "Alt text") to the Image media type — use the alias `umbracoAltText` for zero config, or
- Point the package at your existing alias with `AltTextAssistant:AltTextPropertyAlias` (see below).

If no matching property is found the dashboard shows a clear warning and saving is blocked, so it never silently fails.

## Installation

```
dotnet add package DigitalWonderlab.AltTextAssistant
```

Restart the site, then open **Media → Alt Text Assistant**.

## Configuration

All configuration lives under an `AltTextAssistant` section in `appsettings.json`. Every setting is optional.

### Alt-text field alias (optional)

```json
{
  "AltTextAssistant": {
    "AltTextPropertyAlias": "umbracoAltText"
  }
}
```

Set this if your Image media type stores alt text under a different property alias.

### AI provider (optional — enables AI suggestions)

AI suggestions are **self-service and bring-your-own-key** — the package makes no AI calls of its own. Until you add a key the AI buttons stay hidden and the in-app notice points editors here; manual alt text entry works throughout. You supply a key from your own provider account, calls are billed to you, and images are sent only to the provider you configure (see [Privacy](#privacy)).

Add a provider API key. Only one is needed; if several are set the priority is **Anthropic → OpenAI → Google**.

```json
{
  "AltTextAssistant": {
    "AnthropicApiKey": "sk-ant-...",
    "OpenAIApiKey": "",
    "GoogleApiKey": ""
  }
}
```

The model can be overridden per provider (defaults shown):

```json
{
  "AltTextAssistant": {
    "AnthropicApiKey": "sk-ant-...",
    "AnthropicModel": "claude-haiku-4-5-20251001",
    "OpenAIModel": "gpt-4o",
    "GoogleModel": "gemini-2.0-flash"
  }
}
```

Keep API keys out of source control — use user secrets, environment variables, or your host's app settings / key vault rather than committing `appsettings.json`.

Without any key configured the AI buttons are hidden and the dashboard works for manual alt text entry.

## How it works

- `GET /umbraco/management/api/v1/alt-text-assistant/images` — pages the media library for images with no alt text
- `POST /umbraco/management/api/v1/alt-text-assistant/save` — writes the alt-text property back to a media item
- `POST /umbraco/management/api/v1/alt-text-assistant/suggest` — reads the image and calls the configured AI provider
- `GET /umbraco/management/api/v1/alt-text-assistant/config` — reports whether AI is enabled, the alt-text alias, and whether that field exists

All endpoints require an authenticated backoffice user.

## Privacy

When you use AI suggestions, the image is sent to the AI provider you configured (Anthropic, OpenAI, or Google) using your own API key, under your own account and their terms. No data is sent anywhere unless you click Suggest, and nothing is sent if no key is configured.

## Issues / Suggestions

Please use the GitHub issue tracker: https://github.com/dwlkev/DigitalWonderlab.AltTextAssistant/issues

## Licence

MIT © Digital Wonderlab
