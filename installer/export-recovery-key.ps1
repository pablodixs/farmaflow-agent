param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = "Stop"
$serviceRoot = Join-Path $env:ProgramData "FarmaFlow\Server"
$secrets = Get-Content (Join-Path $serviceRoot "secrets.json") -Raw | ConvertFrom-Json
$content = @"
FarmaFlow Backup Recovery Key
Server: $env:COMPUTERNAME
ExportedAt: $([DateTimeOffset]::Now.ToString("O"))
BackupKey: $($secrets.BackupKey)
"@
Set-Content -Path $Destination -Value $content -Encoding UTF8
Write-Host "Chave exportada para $Destination. Guarde-a fora do servidor e proteja o arquivo."
