param(
    [Parameter(Mandatory = $true)][string]$Server,
    [int]$Port = 8443,
    [Parameter(Mandatory = $true)][string]$CertificateSha256
)

$ErrorActionPreference = "Stop"
$expected = ($CertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($expected.Length -ne 64) { throw "Impressão digital SHA-256 inválida." }

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.ServerCertificateCustomValidationCallback = {
    param($request, $certificate, $chain, $errors)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $actual = ([BitConverter]::ToString($sha256.ComputeHash($certificate.RawData))).Replace('-', '')
    } finally {
        $sha256.Dispose()
    }
    return $actual -eq $expected
}
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(10)

$serverInfo = $client.GetStringAsync("https://${Server}:$Port/.well-known/farmaflow/server").GetAwaiter().GetResult() | ConvertFrom-Json
if ($serverInfo.certificateSha256 -ne $expected) { throw "O servidor anunciou um certificado diferente do certificado TLS." }
$deployment = $client.GetStringAsync("https://${Server}:$Port/backend/public/deployment").GetAwaiter().GetResult() | ConvertFrom-Json
if ($deployment.mode -ne "LOCAL_SINGLE_STORE") { throw "O backend não está em LOCAL_SINGLE_STORE." }
if ($deployment.server.databaseMajorVersion -ne 17) { throw "A versão principal do PostgreSQL não é 17." }

$unexpectedPorts = @(54329, 8180, 3100)
$exposed = @()
foreach ($internalPort in $unexpectedPorts) {
    $socket = New-Object Net.Sockets.TcpClient
    try {
        $connected = $socket.ConnectAsync($Server, $internalPort).Wait(800)
        if ($connected -and $socket.Connected) { $exposed += $internalPort }
    } catch {} finally { $socket.Dispose() }
}
if ($exposed.Count -gt 0) { throw "Portas internas expostas na rede: $($exposed -join ', ')." }

[ordered]@{
    status = "PASS"
    server = $serverInfo.serverId
    version = $serverInfo.version
    deploymentMode = $deployment.mode
    publicPort = $Port
    internalPortsBlocked = $unexpectedPorts
    certificateSha256 = $expected
} | ConvertTo-Json
