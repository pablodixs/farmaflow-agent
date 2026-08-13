# FarmaFlow Agent

Companion Windows do FarmaFlow. A aplicação roda por usuário na bandeja do
Windows, expõe uma API somente em `127.0.0.1:3333`, integra impressoras locais e
mantém uma outbox SQLite para a evolução do modo offline.

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

Pull requests e pushes em `master` executam o build `win-x64` no GitHub
Actions. Uma tag como `v1.0.0` publica automaticamente:

- `FarmaFlowAgent-Setup.exe`
- `SHA256SUMS.txt`

O instalador é autocontido, não exige runtime .NET previamente instalado e
configura a inicialização do agente no login do usuário.
