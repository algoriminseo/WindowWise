[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 5047)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\WindowWise.Signaling\WindowWise.Signaling.csproj'
$taskKeyId = Read-Host 'Cloudflare TURN Token ID'
$taskToken = Read-Host 'Cloudflare API Token (hidden)' -AsSecureString
if ([string]::IsNullOrWhiteSpace($taskKeyId) -or $taskToken.Length -eq 0) {
    $taskToken.Dispose()
    throw 'A TURN Token ID and API token are required.'
}

$taskRandom = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$taskBytes = New-Object byte[] 32
$taskRandom.GetBytes($taskBytes)
$taskRandom.Dispose()
$taskAccessKey = [Convert]::ToBase64String($taskBytes)
$taskPrevious = @{}
foreach ($taskName in @('CLOUDFLARE_TURN_KEY_ID', 'CLOUDFLARE_TURN_API_TOKEN', 'WINDOWWISE_TURN_ACCESS_KEY')) {
    $taskPrevious[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
}

try {
    $env:CLOUDFLARE_TURN_KEY_ID = $taskKeyId.Trim()
    $env:CLOUDFLARE_TURN_API_TOKEN = [System.Net.NetworkCredential]::new('', $taskToken).Password
    $env:WINDOWWISE_TURN_ACCESS_KEY = $taskAccessKey
    Write-Host ''
    Write-Host 'App TEST ACCESS KEY (share privately with your test peer):'
    Write-Host $taskAccessKey
    Write-Host ''
    Write-Host "Local signaling: ws://localhost:$Port/signal"
    Write-Host "For remote testing, in another terminal: cloudflared tunnel --url http://127.0.0.1:$Port"
    Write-Host 'Use wss://<tunnel-host>/signal in BOTH apps. Leave the credential endpoint blank.'
    Write-Host 'Keep this terminal open. The access key changes each time this script starts.'
    & dotnet run --project $taskProject --no-launch-profile -- --urls "http://127.0.0.1:$Port"
    if ($LASTEXITCODE -ne 0) { throw "Signaling server exited with code $LASTEXITCODE." }
}
finally {
    foreach ($taskName in $taskPrevious.Keys) {
        [Environment]::SetEnvironmentVariable($taskName, $taskPrevious[$taskName], 'Process')
    }
    $taskToken.Dispose()
    $taskAccessKey = $null
}
