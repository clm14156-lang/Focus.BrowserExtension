param([switch]$Demo)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot $(if ($Demo) { 'artifacts\demo-host' } else { 'artifacts\host' })
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
dotnet publish (Join-Path $projectRoot 'native-host\FocusApp.BrowserBridge.csproj') -c Release --self-contained false -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
$demoMarker = Join-Path $outputDirectory 'demo.enabled'
if ($Demo) { Set-Content -LiteralPath $demoMarker -Value 'Explicit development fixture; not WPF data.' -Encoding utf8 }
elseif (Test-Path -LiteralPath $demoMarker) { Remove-Item -LiteralPath $demoMarker }
Write-Output "Host directory: $outputDirectory"
