param(
    [string]$ChromeExtensionId,
    [string]$EdgeExtensionId,
    [string]$HostDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\host'),
    [switch]$ReplaceExisting
)
$ErrorActionPreference = 'Stop'
$ids = @($ChromeExtensionId, $EdgeExtensionId) | Where-Object { $_ } | Select-Object -Unique
if (!$ids -or @($ids | Where-Object { $_ -cnotmatch '^[a-p]{32}$' }).Count) { throw 'Provide actual Chrome/Edge extension IDs (32 letters a-p).' }
$resolvedHostDirectory = (Resolve-Path -LiteralPath $HostDirectory).Path
$executable = Join-Path $resolvedHostDirectory 'FocusApp.BrowserBridge.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Build the host first using Build-Host.ps1.' }
$manifestPath = Join-Path $resolvedHostDirectory 'com.focusapp.browser_bridge.json'
$registryPaths = @()
if ($ChromeExtensionId) { $registryPaths += 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.focusapp.browser_bridge' }
if ($EdgeExtensionId) { $registryPaths += 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.focusapp.browser_bridge' }
foreach ($registryPath in $registryPaths) {
    if (Test-Path -LiteralPath $registryPath) {
        $existingPath = (Get-Item -LiteralPath $registryPath).GetValue('')
        if ($existingPath -and $existingPath -ne $manifestPath -and !$ReplaceExisting) { throw "Existing host points to $existingPath. Use -ReplaceExisting only if intentionally replacing it." }
    }
}
$manifest = [ordered]@{
    name = 'com.focusapp.browser_bridge'
    description = 'FocusApp read-only browser bridge'
    path = $executable
    type = 'stdio'
    allowed_origins = @($ids | ForEach-Object { "chrome-extension://$_/" })
}
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
foreach ($registryPath in $registryPaths) {
    New-Item -Path $registryPath -Force | Out-Null
    Set-Item -LiteralPath $registryPath -Value $manifestPath
}
if (Test-Path -LiteralPath (Join-Path $resolvedHostDirectory 'demo.enabled')) { Write-Warning 'DEMO HOST: data is simulated, not from WPF.' }
Write-Output "Registered for current Windows user: $manifestPath"
