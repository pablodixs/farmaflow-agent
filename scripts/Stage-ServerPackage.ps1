param(
    [Parameter(Mandatory = $true)][string]$BackendJar,
    [Parameter(Mandatory = $true)][string]$WebStandalone,
    [Parameter(Mandatory = $true)][string]$JavaRuntime,
    [Parameter(Mandatory = $true)][string]$NodeRuntime,
    [Parameter(Mandatory = $true)][string]$PostgresRuntime,
    [string]$Version = "development",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\publish-server")
)

$ErrorActionPreference = "Stop"
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$required = @(
    $BackendJar,
    (Join-Path $WebStandalone "server.js"),
    (Join-Path $JavaRuntime "bin\java.exe"),
    (Join-Path $NodeRuntime "node.exe"),
    (Join-Path $PostgresRuntime "bin\postgres.exe"),
    (Join-Path $PostgresRuntime "bin\pg_dump.exe"),
    (Join-Path $PostgresRuntime "bin\pg_restore.exe")
)
foreach ($path in $required) {
    if (-not (Test-Path $path)) { throw "Componente obrigatório não encontrado: $path" }
}

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

dotnet publish (Join-Path $PSScriptRoot "..\src\FarmaFlow.Server.Host\FarmaFlow.Server.Host.csproj") `
    -c Release -r win-x64 --self-contained true -p:Version=$Version -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "A publicação do Host falhou." }

$appSettingsPath = Join-Path $OutputDirectory "appsettings.json"
if (Test-Path $appSettingsPath) {
    $appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $appSettings.ServerHost.Version = $Version
    $appSettings | ConvertTo-Json -Depth 10 | Set-Content $appSettingsPath -Encoding UTF8
}

$runtime = Join-Path $OutputDirectory "runtime"
New-Item -ItemType Directory -Force -Path (Join-Path $runtime "backend"), (Join-Path $runtime "web"), (Join-Path $runtime "node") | Out-Null
Copy-Item $BackendJar (Join-Path $runtime "backend\app.jar")
Copy-Item (Join-Path $WebStandalone "*") (Join-Path $runtime "web") -Recurse
Copy-Item $JavaRuntime (Join-Path $runtime "java") -Recurse
Copy-Item (Join-Path $NodeRuntime "node.exe") (Join-Path $runtime "node\node.exe") -Force
Copy-Item $PostgresRuntime (Join-Path $runtime "postgres") -Recurse

$manifest = [ordered]@{
    product = "FarmaFlow Server"
    version = $Version
    createdAt = [DateTimeOffset]::UtcNow.ToString("O")
    components = [ordered]@{
        java = (& (Join-Path $runtime "java\bin\java.exe") -version 2>&1 | Select-Object -First 1).ToString()
        node = (& (Join-Path $runtime "node\node.exe") --version).Trim()
        postgres = (& (Join-Path $runtime "postgres\bin\postgres.exe") --version).Trim()
        backendSha256 = (Get-FileHash (Join-Path $runtime "backend\app.jar") -Algorithm SHA256).Hash
        webServerSha256 = (Get-FileHash (Join-Path $runtime "web\server.js") -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory "distribution-manifest.json") -Encoding UTF8
Write-Host "Pacote de servidor preparado em $OutputDirectory"
