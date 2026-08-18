#!/bin/bash

PACKAGE="DigitalWonderlab.AltTextAssistant"
ROOT="C:\Users\KevinTriggle\source\repos\umbraco-plugins\DigitalWonderlab.AltTextAssistant"
CSPROJ="$ROOT\\$PACKAGE.csproj"
OUTPUT_DIR="$ROOT\\bin\Release"

# Get version from .csproj
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -n 1 | tr -d '\r')
NUPKG="$OUTPUT_DIR/$PACKAGE.$VERSION.nupkg"

echo "=== Publish $PACKAGE v$VERSION to NuGet ==="
echo ""

# Check for API key
if [ -z "$NUGET_API_KEY" ]; then
    echo "NUGET_API_KEY not set."
    echo ""
    echo "Either export it first:"
    echo "  export NUGET_API_KEY=your-key-here"
    echo "  bash publish-nuget.sh"
    echo ""
    echo "Or pass it inline:"
    echo "  NUGET_API_KEY=your-key-here bash publish-nuget.sh"
    echo ""
    echo "Get your key from: https://www.nuget.org/account/apikeys"
    exit 1
fi

# Build and pack
echo "=== Building and packing ==="
dotnet pack "$CSPROJ" -c Release -v quiet
if [ $? -ne 0 ]; then
    echo "PACK FAILED - aborting"
    exit 1
fi

# Verify .nupkg exists
if [ ! -f "$NUPKG" ]; then
    echo "Expected package not found: $NUPKG"
    exit 1
fi
echo "Package: $NUPKG"
echo ""

# Confirm before pushing
echo "About to push $PACKAGE v$VERSION to nuget.org"
read -p "Continue? (y/n) " -n 1 -r
echo ""
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
fi

# Push
echo ""
echo "=== Pushing to NuGet ==="
dotnet nuget push "$NUPKG" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json

if [ $? -eq 0 ]; then
    echo ""
    echo "=== Published successfully ==="
    echo "https://www.nuget.org/packages/$PACKAGE/$VERSION"
else
    echo ""
    echo "=== Push failed ==="
    exit 1
fi
