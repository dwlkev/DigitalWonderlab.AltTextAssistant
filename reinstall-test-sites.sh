#!/bin/bash

PACKAGE="DigitalWonderlab.AltTextAssistant"
ROOT="C:\Users\KevinTriggle\source\repos\umbraco-plugins\DigitalWonderlab.AltTextAssistant"
SOURCE="$ROOT\\bin\Release"
SITES=(
    "C:\Users\KevinTriggle\source\repos\umbraco-plugins\v17-test-site\MyProject"
)

# Get version from .csproj (portable: avoid grep -P, which can fail by locale/shell build)
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT\\$PACKAGE.csproj" | head -n 1 | tr -d '\r')
echo "=== Package version: $VERSION ==="
echo ""

# Build and pack first
echo "=== Packing $PACKAGE v$VERSION ==="
dotnet pack "$ROOT" -c Release --no-restore -v quiet
if [ $? -ne 0 ]; then
    echo "PACK FAILED - aborting"
    exit 1
fi
echo ""

# Clear cached version so dotnet doesn't use a stale copy
echo "=== Clearing NuGet cache for $PACKAGE ==="
CACHE_DIR="$(dotnet nuget locals global-packages -l | sed 's/.*: //')/${PACKAGE,,}/$VERSION"
if [ -d "$CACHE_DIR" ]; then
    rm -rf "$CACHE_DIR"
    echo "Cleared: $CACHE_DIR"
else
    echo "No cached version found"
fi
echo ""

for SITE in "${SITES[@]}"; do
    SITE_NAME=$(basename "$(dirname "$SITE")")
    echo "=== $SITE_NAME ==="

    # Remove (ignore error if not installed)
    dotnet remove "$SITE" package $PACKAGE 2>/dev/null

    # Clean App_Plugins/AltTextAssistant left behind from previous install
    APP_PLUGINS="$SITE/App_Plugins/AltTextAssistant"
    if [ -d "$APP_PLUGINS" ]; then
        rm -rf "$APP_PLUGINS"
        echo "  Cleaned old App_Plugins/AltTextAssistant"
    fi

    # Install the nupkg to global cache so restore finds it
    dotnet nuget push "$SOURCE/$PACKAGE.$VERSION.nupkg" --source "$(dotnet nuget locals global-packages -l | sed 's/.*: //')" 2>/dev/null || true

    # Add package reference then restore normally (uses nuget.config + global cache)
    dotnet add "$SITE" package $PACKAGE --version "$VERSION" --no-restore
    dotnet restore "$SITE"

    if [ $? -eq 0 ]; then
        echo "  OK"
    else
        echo "  FAILED"
    fi
    echo ""
done

echo "=== Done. Restart each site to test. ==="
