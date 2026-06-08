# FarmaFlow Agent

Agente local do FarmaFlow para impressao ESC/POS, consulta de impressoras,
status do computador, tray na bandeja do Windows e verificacao de atualizacao
OTA via manifesto remoto.

## Requisitos

- Windows 10 ou superior
- Java/JDK 21 para desenvolvimento e empacotamento
- Maven Wrapper incluido no projeto (`mvnw.cmd`)
- Impressora termica instalada no Windows

Para gerar instalador `.exe` com assistente, tambem e necessario instalar o
WiX Toolset e adicionar `candle.exe` e `light.exe` ao `PATH`.

## Rodar em desenvolvimento

```powershell
.\mvnw.cmd spring-boot:run
```

O agente sobe por padrao em:

```text
http://localhost:3333
```

## Configuracao

As configuracoes ficam em:

```text
src/main/resources/application.properties
```

Opcoes atuais:

```properties
spring.application.name=farmaflow-agent
server.port=3333
agent.version=0.0.1-SNAPSHOT
agent.internet-check.url=https://www.google.com/generate_204
agent.internet-check.timeout-ms=3000
agent.tray.icon-path=
agent.update.manifest-url=
```

- `server.port`: porta local da API.
- `agent.version`: versao atual usada na checagem OTA.
- `agent.internet-check.url`: URL usada para testar internet.
- `agent.internet-check.timeout-ms`: timeout do teste de internet.
- `agent.tray.icon-path`: caminho absoluto para um icone personalizado do tray.
- `agent.update.manifest-url`: URL do manifesto remoto de atualizacao.

## Tray

Ao iniciar em ambiente grafico, o agente cria um icone na bandeja do Windows.
O menu inclui:

- Abrir painel
- Status do dispositivo
- Verificar atualizacao
- Sair

Para usar uma imagem personalizada no tray, coloque um arquivo:

```text
src/main/resources/tray-icon.png
```

Ou configure um caminho absoluto:

```properties
agent.tray.icon-path=C:/FarmaFlow/tray-icon.png
```

## Endpoints

### Listar impressoras

```http
GET /print/printers
```

Resposta:

```json
[
  "ELGIN i9",
  "Microsoft Print to PDF"
]
```

### Imprimir venda

```http
POST /print/sale
```

Body:

```json
{
  "type": "sale_receipt",
  "printerName": "ELGIN i9",
  "paperWidth": "80mm",
  "store": {
    "name": "FarmaFlow",
    "cnpj": "00.000.000/0001-00",
    "address": "Rua Exemplo, 123"
  },
  "customer": null,
  "items": [
    {
      "name": "Produto teste",
      "quantity": 1,
      "unitPrice": 10.50,
      "total": 10.50
    }
  ],
  "total": 10.50,
  "paymentMethod": "Dinheiro"
}
```

### Imprimir recibo de entrega

```http
POST /print/delivery
```

Body:

```json
{
  "type": "delivery_receipt",
  "printerName": "ELGIN i9",
  "paperWidth": "80mm",
  "store": {
    "name": "FarmaFlow",
    "cnpj": "00.000.000/0001-00",
    "address": "Rua Exemplo, 123"
  },
  "customer": {
    "name": "Cliente Teste",
    "phone": "(11) 99999-9999",
    "address": "Endereco de entrega"
  },
  "items": [
    {
      "name": "Produto teste",
      "quantity": 1,
      "unitPrice": 10.50,
      "total": 10.50
    }
  ],
  "total": 10.50,
  "paymentMethod": "Pix"
}
```

### Teste de impressora

```http
POST /print/test
```

Body:

```json
{
  "printerName": "ELGIN i9",
  "paperWidth": "80mm"
}
```

`paperWidth` aceita `58mm` ou `80mm`.

### Status do dispositivo

```http
GET /agent/status
```

Resposta:

```json
{
  "operatingSystem": {
    "name": "Windows 10",
    "version": "10.0",
    "architecture": "amd64"
  },
  "hardware": {
    "computerName": "PC-CAIXA-01",
    "availableProcessors": 8,
    "maxMemoryBytes": 4294967296,
    "totalMemoryBytes": 268435456,
    "freeMemoryBytes": 123456789
  },
  "internet": {
    "connected": true,
    "checkedUrl": "https://www.google.com/generate_204",
    "latencyMs": 120,
    "error": null
  }
}
```

### Verificar atualizacao OTA

```http
GET /agent/update/check
```

Configure antes:

```properties
agent.update.manifest-url=https://seu-dominio.com/farmaflow-agent/update.json
```

Manifesto esperado:

```json
{
  "version": "0.0.2",
  "downloadUrl": "https://seu-dominio.com/farmaflow-agent-0.0.2.exe",
  "releaseNotes": "Correcoes e melhorias"
}
```

Resposta:

```json
{
  "currentVersion": "0.0.1-SNAPSHOT",
  "configured": true,
  "updateAvailable": true,
  "latestVersion": "0.0.2",
  "downloadUrl": "https://seu-dominio.com/farmaflow-agent-0.0.2.exe",
  "releaseNotes": "Correcoes e melhorias",
  "error": null
}
```

## Gerar build

Para gerar o `.jar` executavel:

```powershell
.\mvnw.cmd clean package
```

Arquivo gerado:

```text
target/farmaflow-agent-0.0.1-SNAPSHOT.jar
```

## Publicar `.jar` no GitHub Releases

O projeto possui workflow de release em `.github/workflows/release.yml`.

Para publicar automaticamente no GitHub Releases:

1. Crie uma tag no formato `v*` (ex.: `v0.0.1`) e envie para o repositório.
2. O workflow **Release JAR** irá executar `mvn clean package`.
3. O arquivo `target/farmaflow-agent-*.jar` será anexado no release criado da tag.

Para testar:

```powershell
javaw -jar target\farmaflow-agent-0.0.1-SNAPSHOT.jar
```

## Gerar executavel portatil

Use o script:

```powershell
.\scripts\package-windows.ps1
```

Saida:

```text
dist-portable/FarmaFlowAgent/FarmaFlowAgent.exe
```

Distribua a pasta inteira `dist-portable/FarmaFlowAgent`, nao apenas o `.exe`,
porque ela contem o runtime Java e os arquivos do app.

## Gerar instalador Windows

Instale o WiX Toolset e garanta que `candle.exe` e `light.exe` estejam no
`PATH`.

Depois rode:

```powershell
.\scripts\package-windows.ps1 -Installer
```

Saida esperada:

```text
dist-installer/FarmaFlowAgent-0.0.1.exe
```

## Iniciar junto com o Windows

Como o agente usa tray, o ideal e iniciar no login do usuario, nao como servico.

Crie um atalho para:

```text
dist-portable/FarmaFlowAgent/FarmaFlowAgent.exe
```

E coloque em:

```text
shell:startup
```

No Windows, pressione `Win + R`, digite `shell:startup` e copie o atalho para a
pasta aberta.

## Testes

```powershell
.\mvnw.cmd test
```

## Observacoes

- O tray so aparece em ambiente grafico com suporte a `SystemTray`.
- A checagem OTA atual apenas consulta o manifesto e informa se ha versao nova.
- A instalacao automatica de update ainda precisa de uma estrategia de release,
  assinatura/validacao e fluxo seguro de substituicao do executavel.
