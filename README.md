# FarmaFlow Local

Distribuição Windows local do FarmaFlow. Este repositório contém a estação com
WebView2 e impressão, o Host do servidor local, a ferramenta administrativa de
migração e os instaladores Inno Setup.

## Requisitos de desenvolvimento

- Windows 10 ou 11 x64
- .NET SDK 8
- Inno Setup 6 apenas para gerar o instalador localmente

## Executar

```powershell
dotnet run --project src/FarmaFlow.Agent/FarmaFlow.Agent.csproj
```

As configurações ficam em
`src/FarmaFlow.Agent/appsettings.json`. Para produção, defina `ApiBaseUrl`,
`WebAppUrl` e a lista exata de `AllowedOrigins`.

## Integração local

O frontend inicia uma sessão local por `GET` e `POST /agent/local/handshake`.
Os demais endpoints exigem o token Bearer emitido pelo handshake.

- `GET /agent/health`
- `GET /agent/status`
- `GET /print/printers`
- `POST /print/test`
- `POST /print/pdf`
- `GET /print/pdf/{jobId}/status`
- `POST /offline/operations`

O pareamento da estação é iniciado no app web. O usuário informa o código de
uso único pelo menu **Parear estação** do tray. A credencial devolvida pelo
backend é protegida com DPAPI para o usuário atual do Windows.

## Build e release

Pull requests e pushes em `master` validam os componentes .NET em `win-x64`.
O workflow **Build Windows installers** combina este repositório com
`pablodixs/farmaflow.backend` e `pablodixs/farmaflow`, podendo receber branch,
tag ou commit para cada fonte. Uma tag como `v1.0.0` publica automaticamente:

- `FarmaFlow-Server-Setup.exe`
- `FarmaFlow-Estacao-Setup.exe`
- `FarmaFlow-Migracao-Setup.exe`
- `FarmaFlow-Migration.zip`
- `release-manifest.json`
- `SHA256SUMS.txt`

Os instaladores são autocontidos. O manifesto registra os commits exatos e as
versões dos runtimes usados. O release exige o token somente leitura dos
repositórios privados e o certificado Authenticode; não publica pacotes sem
assinatura. Veja [installer/README.md](installer/README.md) para configurar os
pré-requisitos.

O bundle também publica `FarmaFlow-Migracao-Setup.exe`, o assistente gráfico
para preparar o ensaio/corte sem executar PowerShell manualmente.

Não use `FarmaFlow.Migration.exe` por duplo clique: ele é o CLI de contingência
e o console fecha após exibir a ajuda. No Windows, abra o instalador exato
`FarmaFlow-Migracao-Setup.exe` baixado de uma release, ou
`FarmaFlowMigracaoSetup.exe` no artifact de validação da CI.

Para a operação normal, comece pelo [guia rápido local](docs/GUIA-RAPIDO-LOCAL.md).

O procedimento completo de build, ensaio, migração do Supabase, instalação,
pareamento, go-live, backup e rollback está em
[docs/GUIA-INSTALACAO-COMPLETA.md](docs/GUIA-INSTALACAO-COMPLETA.md).
