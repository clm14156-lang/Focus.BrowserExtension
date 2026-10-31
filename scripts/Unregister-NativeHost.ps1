param([string]$HostDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\host'))
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path ([IO.Path]::GetFullPath($HostDirectory)) 'com.focusapp.browser_bridge.json'
foreach ($registryPath in @('HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.focusapp.browser_bridge', 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.focusapp.browser_bridge')) {
    if ((Test-Path -LiteralPath $registryPath) -and (Get-Item -LiteralPath $registryPath).GetValue('') -eq $manifestPath) {
        Remove-Item -LiteralPath $registryPath
        Write-Output "Removed $registryPath"
    }
}
