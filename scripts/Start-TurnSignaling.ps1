[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 5047)

# Pass the Cloudflare credentials through the process environment. Apps authenticate with their sign-in token.
# Closing this terminal or stopping the server ends the service. Production service registration and secret
# storage are separate concerns.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\WindowWise.Signaling\WindowWise.Signaling.csproj'
$taskKeyId = Read-Host 'Cloudflare TURN Token ID'
$taskToken = Read-Host 'Cloudflare API Token (hidden)' -AsSecureString
if ([string]::IsNullOrWhiteSpace($taskKeyId) -or $taskToken.Length -eq 0) {
    $taskToken.Dispose()
    throw 'A TURN Token ID and API token are required.'
}

# Keep only the current process values for restoration; do not persist tokens to user/system environment
# variables or files.
$taskPrevious = @{}
foreach ($taskName in @('CLOUDFLARE_TURN_KEY_ID', 'CLOUDFLARE_TURN_API_TOKEN')) {
    $taskPrevious[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
}

try {
    $env:CLOUDFLARE_TURN_KEY_ID = $taskKeyId.Trim()
    $env:CLOUDFLARE_TURN_API_TOKEN = [System.Net.NetworkCredential]::new('', $taskToken).Password
    Write-Host ''
    Write-Host "Local signaling: ws://localhost:$Port/signal"
    Write-Host "For remote testing, in another terminal: cloudflared tunnel --url http://127.0.0.1:$Port"
    Write-Host 'Use wss://<tunnel-host>/signal in BOTH apps. Leave the credential endpoint blank.'
    Write-Host 'Keep this terminal open while the signaling server is in use.'
    # Listen on loopback by default. Remote tests use the HTTPS/WSS address of a separate cloudflared tunnel.
    & dotnet run --project $taskProject --no-launch-profile -- --urls "http://127.0.0.1:$Port"
    if ($LASTEXITCODE -ne 0) { throw "Signaling server exited with code $LASTEXITCODE." }
}
finally {
    # Restore the parent PowerShell environment on both normal shutdown and startup failure.
    foreach ($taskName in $taskPrevious.Keys) {
        [Environment]::SetEnvironmentVariable($taskName, $taskPrevious[$taskName], 'Process')
    }
    $taskToken.Dispose()
}
