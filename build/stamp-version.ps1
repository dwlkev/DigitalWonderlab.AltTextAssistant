param([string]$Version, [string]$PluginDir, [switch]$Restore)

$files = @(
    "$PluginDir\umbraco-package.json",
    "$PluginDir\alt-text-assistant-dashboard.js"
)

foreach ($f in $files) {
    if (-not (Test-Path $f)) { continue }
    $content = Get-Content $f -Raw
    if ($Restore) {
        $content = $content.Replace("?v=$Version", '?v=PACKAGE_VERSION')
    } else {
        $content = $content.Replace('PACKAGE_VERSION', $Version)
    }
    Set-Content $f $content -NoNewline
}
