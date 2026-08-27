param(
    [Parameter(Mandatory = $true)][string]$JavaHome,
    [Parameter(Mandatory = $true)][string]$NodeExecutable,
    [Parameter(Mandatory = $true)][string]$PostgresUrl,
    [Parameter(Mandatory = $true)][string]$PostgresSha256,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$DownloadCacheDirectory = (Join-Path $PSScriptRoot "..\.runtime-cache")
)

$ErrorActionPreference = "Stop"
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$DownloadCacheDirectory = [IO.Path]::GetFullPath($DownloadCacheDirectory)
$jlink = Join-Path $JavaHome "bin\jlink.exe"

foreach ($path in @($jlink, $NodeExecutable)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Runtime obrigatório não encontrado: $path"
    }
}

if ($PostgresSha256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw "O SHA-256 esperado do PostgreSQL é inválido."
}

if (Test-Path $OutputDirectory) {
    Remove-Item $OutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutputDirectory, $DownloadCacheDirectory | Out-Null

$javaOutput = Join-Path $OutputDirectory "java"
& $jlink `
    --add-modules ALL-MODULE-PATH `
    --strip-debug `
    --no-header-files `
    --no-man-pages `
    --compress=2 `
    --output $javaOutput
if ($LASTEXITCODE -ne 0) {
    throw "Não foi possível criar o runtime Java 21 com jlink."
}

$nodeOutput = Join-Path $OutputDirectory "node"
New-Item -ItemType Directory -Force -Path $nodeOutput | Out-Null
Copy-Item $NodeExecutable (Join-Path $nodeOutput "node.exe") -Force

$archiveName = Split-Path ([Uri]$PostgresUrl).AbsolutePath -Leaf
$archive = Join-Path $DownloadCacheDirectory $archiveName
if (-not (Test-Path $archive -PathType Leaf)) {
    Invoke-WebRequest -Uri $PostgresUrl -OutFile $archive
}

$actualSha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $PostgresSha256.ToLowerInvariant()) {
    Remove-Item $archive -Force
    throw "SHA-256 do PostgreSQL divergente. Esperado: $PostgresSha256; obtido: $actualSha256"
}

$temporaryRoot = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [IO.Path]::GetTempPath()
} else {
    $env:RUNNER_TEMP
}
$extractDirectory = Join-Path $temporaryRoot "farmaflow-postgres"
if (Test-Path $extractDirectory) {
    Remove-Item $extractDirectory -Recurse -Force
}

try {
    Expand-Archive -Path $archive -DestinationPath $extractDirectory -Force
    $postgresSource = Join-Path $extractDirectory "pgsql"
    $postgresOutput = Join-Path $OutputDirectory "postgres"
    New-Item -ItemType Directory -Force -Path $postgresOutput | Out-Null

    foreach ($directory in @("bin", "lib", "share")) {
        $source = Join-Path $postgresSource $directory
        if (-not (Test-Path $source -PathType Container)) {
            throw "Diretório $directory ausente no pacote oficial do PostgreSQL."
        }
        Copy-Item $source (Join-Path $postgresOutput $directory) -Recurse
    }

    Get-ChildItem $postgresSource -File -Filter "*license*.txt" |
        Copy-Item -Destination $postgresOutput
}
finally {
    if (Test-Path $extractDirectory) {
        Remove-Item $extractDirectory -Recurse -Force
    }
}

$required = @(
    (Join-Path $javaOutput "bin\java.exe"),
    (Join-Path $nodeOutput "node.exe"),
    (Join-Path $OutputDirectory "postgres\bin\postgres.exe"),
    (Join-Path $OutputDirectory "postgres\bin\initdb.exe"),
    (Join-Path $OutputDirectory "postgres\bin\pg_dump.exe"),
    (Join-Path $OutputDirectory "postgres\bin\pg_restore.exe")
)
foreach ($path in $required) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Runtime preparado está incompleto: $path"
    }
}

Write-Host "Runtimes verificados e preparados em $OutputDirectory"
