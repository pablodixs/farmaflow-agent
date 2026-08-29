# Guia completo de instalação do FarmaFlow local

Este guia descreve o processo completo para gerar os instaladores, ensaiar a migração, transferir os dados do Supabase, instalar um servidor por loja, configurar as estações e concluir o corte com segurança.

> **Regra principal:** nunca permita gravações simultâneas no Supabase e no servidor local. Antes da primeira venda local, ainda é possível cancelar o corte e reabrir o ambiente cloud. Depois da primeira gravação local, o banco local passa a ser a fonte oficial e o Supabase não deve ser reaberto para operação.

## 1. Escopo e arquitetura

Cada loja recebe um servidor e um banco PostgreSQL independentes.

| Componente | Onde é instalado | Porta | Exposição |
| --- | --- | ---: | --- |
| FarmaFlow Host HTTPS | Servidor da loja | 8443 | Rede privada |
| PostgreSQL 17 | Servidor da loja | 54329 | Somente `127.0.0.1` |
| Backend Spring Boot | Servidor da loja | 8180 | Somente `127.0.0.1` |
| Frontend Next.js | Servidor da loja | 3100 | Somente `127.0.0.1` |
| FarmaFlow Estação | Cada computador de atendimento | 3333 | Somente `127.0.0.1` |

O Host recebe as conexões HTTPS em `8443`, encaminha `/backend` ao Spring e encaminha as demais rotas ao Next.js. A estação fixa a impressão digital SHA-256 do certificado do servidor e bloqueia certificados diferentes.

O modo local é `LOCAL_SINGLE_STORE`. O servidor deve conter exatamente uma loja; operações novas entre lojas ficam bloqueadas e transferências antigas preservam um snapshot da contraparte.

## 2. Regras de segurança

- Não coloque senhas, chaves de recuperação, arquivos `.ffbackup` ou `secrets.json` em Git, e-mail ou mensagens.
- Execute a migração com uma conta administrativa dedicada e encerre o terminal ao terminar.
- Guarde a senha de cada pacote de migração em um gerenciador de senhas separado do arquivo.
- Copie a chave de recuperação dos backups para uma mídia externa protegida. Sem essa chave, os backups diários não podem ser restaurados.
- Não pause nem exclua o projeto Supabase durante os 90 dias de retenção.
- Não habilite RLS indiscriminadamente antes do corte. O backend usa JDBC e políticas inadequadas podem interromper a aplicação.
- Não exponha as portas `54329`, `8180` ou `3100` no firewall ou no roteador.
- Não encaminhe a porta `8443` para a internet. Ela deve existir apenas na rede privada da loja.

## 3. Responsáveis e registros

Antes do ensaio, designe:

- responsável pelo Supabase e pelo backend cloud;
- responsável por cada servidor Windows;
- responsável pela validação financeira e de estoque;
- responsável pela decisão go/no-go;
- local seguro para os pacotes, manifestos, senhas e chaves de recuperação.

Crie um registro de corte contendo:

- versão da release;
- commits de backend, frontend e agente;
- identificador e nome de cada loja;
- horário de início da manutenção e do snapshot;
- hashes dos arquivos utilizados;
- resultado do ensaio, da reconciliação e dos testes;
- horário da primeira venda local;
- decisão go/no-go e nome do responsável.

## 4. Pré-requisitos

### 4.1 Servidor de cada loja

- Windows x64 atualizado, com acesso de administrador local;
- nome definitivo do computador antes da ativação;
- endereço IP estável ou reserva DHCP;
- perfil de rede do Windows definido como **Privado**;
- horário e fuso configurados corretamente;
- energia protegida por nobreak;
- pasta ou unidade externa para a segunda cópia dos backups;
- comunicação TCP da estação para o servidor na porta `8443`.

Defina o nome e o IP antes da ativação porque o certificado é emitido para o nome e os endereços presentes naquele momento.

### 4.2 Estações

- Windows 10 ou 11 x64 atualizado;
- acesso à porta `8443` do servidor da loja;
- impressoras e drivers já instalados no Windows;
- um usuário Windows por operador ou por estação, conforme a política da loja.

O instalador da estação é por usuário e grava dados em `%LocalAppData%\FarmaFlow\Agent`.

### 4.3 Migração

- conexão com o PostgreSQL do Supabase;
- senha do papel PostgreSQL usado na exportação;
- PostgreSQL 17 e a ferramenta `FarmaFlow.Migration.exe` no computador de migração;
- espaço livre para o dump integral, dois bancos de staging e os dois pacotes de loja;
- acesso à internet durante `archive-media`;
- UUID de cada loja.

Para `pg_dump`, prefira a conexão direta exibida em **Connect** no painel do Supabase. Ela usa IPv6 por padrão; em uma rede somente IPv4, use o pooler compartilhado em modo de sessão, porta `5432`. Não use o pooler em modo de transação, porta `6543`, para o snapshot. Consulte [Conectar ao Postgres](https://supabase.com/docs/guides/database/connecting-to-postgres).

## 5. Gerar os instaladores no GitHub

O workflow está no repositório `farmaflow-agent`, em `.github/workflows/release.yml`.

### 5.1 Configurar o repositório

Se backend ou frontend forem privados, crie o secret `FARMAFLOW_REPOS_TOKEN` com um fine-grained personal access token que tenha apenas `Contents: read` nos repositórios:

- `pablodixs/farmaflow.backend`;
- `pablodixs/farmaflow`.

Para assinar os instaladores, configure também:

- `WINDOWS_SIGNING_CERT_BASE64`: conteúdo Base64 do PFX;
- `WINDOWS_SIGNING_CERT_PASSWORD`: senha do PFX.

Sem esses secrets de assinatura, os instaladores são gerados sem Authenticode. Não use um instalador sem assinatura em produção sem uma aprovação registrada.

### 5.2 Executar o workflow

1. Abra **Actions** no repositório `farmaflow-agent`.
2. Selecione **Build Windows installers**.
3. Selecione **Run workflow**.
4. Informe a versão sem o prefixo `v`, por exemplo `1.0.0`.
5. Informe uma branch, tag ou SHA para `backend_ref` e `web_ref`. Para uma release de corte, prefira SHAs já testados.
6. Aguarde o job `Build installers (win-x64)` terminar com sucesso.
7. Baixe o artifact `FarmaFlow-Windows-<versão>`.

Uma tag `vX.Y.Z` também executa o workflow e cria uma GitHub Release. A execução manual mantém o artifact por 30 dias.

> O artifact `FarmaFlow-Migration-win-x64` da CI é diferente do instalador de
> release: ele contém `FarmaFlow.Migration.exe` (CLI de contingência) e,
> a partir das próximas execuções, `FarmaFlowMigracaoSetup.exe` (assistente de
> validação, com PostgreSQL 17 portátil). O instalador normal chama-se exatamente
> `FarmaFlow-Migracao-Setup.exe` e fica no artifact `FarmaFlow-Windows-<versão>`.

Os artifacts `FarmaFlow-Estacao-win-x64` e `FarmaFlow-Server-Host-win-x64`
também são componentes brutos para diagnóstico. O primeiro executa apenas o
agente de tray; o segundo é o Host que depende de todo o runtime do servidor.
Nenhum dos dois deve ser aberto por duplo clique para uma instalação normal.

### 5.3 Conferir o bundle

O bundle deve conter:

- `FarmaFlow-Server-Setup.exe`;
- `FarmaFlow-Estacao-Setup.exe`;
- `FarmaFlow-Migracao-Setup.exe`;
- `FarmaFlow-Migration.zip`;
- `release-manifest.json`;
- `SHA256SUMS.txt`.

No PowerShell, confira os hashes:

```powershell
Set-Location "C:\FarmaFlow\Release"
Get-Content .\SHA256SUMS.txt
Get-FileHash .\FarmaFlow-Server-Setup.exe -Algorithm SHA256
Get-FileHash .\FarmaFlow-Estacao-Setup.exe -Algorithm SHA256
Get-FileHash .\FarmaFlow-Migration.zip -Algorithm SHA256
Get-FileHash .\release-manifest.json -Algorithm SHA256
```

Cada resultado deve coincidir com `SHA256SUMS.txt`. Confira também os três commits em `release-manifest.json`.

Se Authenticode estiver configurado:

```powershell
Get-AuthenticodeSignature .\FarmaFlow-Server-Setup.exe
Get-AuthenticodeSignature .\FarmaFlow-Estacao-Setup.exe
```

O campo `Status` deve ser `Valid`.

Em um repositório público, a proveniência também pode ser conferida com GitHub CLI:

```powershell
gh attestation verify .\FarmaFlow-Server-Setup.exe --repo pablodixs/farmaflow-agent
gh attestation verify .\FarmaFlow-Estacao-Setup.exe --repo pablodixs/farmaflow-agent
```

## 6. Fazer um ensaio completo

Não use o primeiro corte real como teste. Faça um ensaio com uma cópia atual do Supabase e servidores descartáveis ou isolados.

O ensaio deve executar as mesmas etapas do corte:

1. exportar o arquivo integral;
2. gerar um pacote separado para cada loja;
3. instalar e restaurar cada servidor;
4. instalar e parear estações;
5. validar dados, operação sem internet, impressão, backup e restauração;
6. registrar duração, problemas e correções;
7. descartar os bancos de ensaio somente depois de aprovar o relatório.

## 7. Preparar o Supabase

### 7.1 Registrar a situação inicial

No SQL Editor do Supabase, registre as lojas:

```sql
select id, name, organization_id
from public.stores
order by name;
```

Registre também a versão do schema:

```sql
select version, description, installed_on, success
from public.flyway_schema_history
order by installed_rank desc
limit 10;
```

No fluxo guiado, o assistente lista as lojas e grava os identificadores no
relatório sem credenciais. Só use UUIDs manualmente no procedimento de
contingência abaixo.

### 7.2 Criar o primeiro arquivo integral

Para o fluxo guiado, execute `FarmaFlow-Migracao-Setup.exe` como suporte. O
`FarmaFlow-Migration.zip` continua disponível como contingência técnica. Crie
uma pasta protegida para os pacotes, por exemplo `D:\FarmaFlow-Corte`.

Exemplo usando a conexão direta:

```powershell
$ffMigration = "C:\FarmaFlow\Migration\FarmaFlow.Migration.exe"
$ffPgBin = "C:\Program Files\PostgreSQL\17\bin"
$ffArchive = "D:\FarmaFlow-Corte\farmaflow-integral-ensaio.ffbackup"

& $ffMigration export-full `
  --host "db.PROJECT_REF.supabase.co" `
  --port 5432 `
  --database postgres `
  --username postgres `
  --pg-bin $ffPgBin `
  --ssl-mode Require `
  --output $ffArchive
```

A ferramenta solicita, sem exibir:

1. senha do PostgreSQL de origem;
2. senha do pacote criptografado;
3. confirmação da senha do pacote.

Ela abre uma transação `REPEATABLE READ, READ ONLY`, exporta um snapshot consistente do schema `public`, valida o catálogo do `pg_restore` e cria:

- `farmaflow-integral-ensaio.ffbackup`;
- `farmaflow-integral-ensaio.ffbackup.json`.

Mantenha os dois arquivos juntos.

Verifique o pacote:

```powershell
& $ffMigration verify --input $ffArchive
```

O comando deve terminar com `Pacote íntegro`.

### 7.3 Fazer a preservação lógica adicional

Backups físicos/PITR do Supabase não são necessariamente baixáveis. A própria documentação orienta criar um backup lógico manual quando é necessário manter uma cópia baixável. Consulte [Backups do banco](https://supabase.com/docs/guides/platform/backups) e [backup e restauração pela CLI](https://supabase.com/docs/guides/platform/migrating-within-supabase/backup-restore).

Em uma máquina com Supabase CLI:

```powershell
$ffDbUrlSecret = Read-Host "Cole a connection string completa" -AsSecureString
$env:SUPABASE_DB_URL = [Net.NetworkCredential]::new("", $ffDbUrlSecret).Password
supabase db dump --db-url $env:SUPABASE_DB_URL -f roles.sql --role-only
supabase db dump --db-url $env:SUPABASE_DB_URL -f schema.sql
supabase db dump --db-url $env:SUPABASE_DB_URL -f data.sql --use-copy --data-only -x "storage.buckets_vectors" -x "storage.vector_indexes"
Remove-Item Env:\SUPABASE_DB_URL
$ffDbUrlSecret.Dispose()
$ffDbUrlSecret = $null
```

Criptografe `roles.sql`, `schema.sql` e `data.sql` com o método aprovado pela empresa e guarde a senha separadamente. Esta cópia é adicional; ela não substitui os pacotes `.ffbackup` usados pelo FarmaFlow.

## 8. Corrigir a exposição do Data API

Execute esta etapa somente depois de criar e validar o primeiro backup lógico.

1. No painel do Supabase, abra as configurações do **Data API**.
2. Retire `public` da lista de schemas expostos.
3. Conecte-se com um papel administrativo.
4. Execute `harden_data_api.sql`, incluído em `FarmaFlow-Migration.zip`.
5. Execute `verify_data_api_security.sql`.
6. Confirme que a consulta de privilégios não retorna concessões para `anon` ou `authenticated` em `public`.
7. Faça uma chamada REST com a chave pública e confirme que nenhum dado de aplicação é devolvido.
8. Teste login, refresh, APIs Spring e Flyway pelo backend cloud.

O endurecimento revoga tabelas, sequências, funções e privilégios padrão desses papéis. Ele não remove o acesso do papel JDBC usado pelo Spring. Alterações recentes do Supabase também caminham para não expor novas tabelas automaticamente, mas isso não elimina privilégios antigos já existentes; consulte o [changelog do Supabase](https://supabase.com/changelog).

## 9. Gerar um pacote para cada loja

Faça este processo em um PostgreSQL 17 temporário. O PostgreSQL instalado pelo `FarmaFlow-Server-Setup.exe` também pode ser usado antes da ativação do servidor.

### 9.1 Preparar as variáveis locais

No servidor, abra PowerShell como administrador:

```powershell
$ffInstall = "C:\Program Files\FarmaFlow Server"
$ffPgBin = Join-Path $ffInstall "runtime\postgres\bin"
$ffMigration = "C:\FarmaFlow\Migration\FarmaFlow.Migration.exe"
$ffSecretsPath = Join-Path $env:ProgramData "FarmaFlow\Server\secrets.json"
$ffSecrets = Get-Content $ffSecretsPath -Raw | ConvertFrom-Json
$ffDbPassword = [string]$ffSecrets.DatabasePassword
```

Não imprima `$ffDbPassword`. Quando a ferramenta solicitar a senha local, cole o valor por um meio controlado e limpe a área de transferência logo depois.

### 9.2 Criar um staging novo

Use um banco novo para cada loja. O nome deve conter `staging`, pois a ferramenta recusa outros nomes.

```powershell
$ffStaging = "farmaflow_staging_loja_1"
$env:PGPASSWORD = $ffDbPassword
& "$ffPgBin\createdb.exe" `
  --host 127.0.0.1 `
  --port 54329 `
  --username farmaflow `
  $ffStaging
Remove-Item Env:\PGPASSWORD
```

Se o banco já existir, pare e identifique sua origem. Não sobrescreva um staging antigo sem confirmar que ele pode ser descartado.

### 9.3 Restaurar o arquivo integral no staging

```powershell
& $ffMigration restore `
  --input "D:\FarmaFlow-Corte\farmaflow-integral-ensaio.ffbackup" `
  --host 127.0.0.1 `
  --port 54329 `
  --database $ffStaging `
  --username farmaflow `
  --pg-bin $ffPgBin
```

Informe a senha do pacote e a senha do PostgreSQL local. A restauração valida o checksum do manifesto, o catálogo do `pg_restore`, o Flyway, as contagens e as reconciliações.

### 9.4 Isolar a loja

```powershell
$ffStoreId = "UUID_DA_LOJA_1"
& $ffMigration filter-store-staging `
  --host 127.0.0.1 `
  --port 54329 `
  --database $ffStaging `
  --username farmaflow `
  --store-id $ffStoreId
```

A ferramenta solicita a senha e exige que você digite o nome exato do banco para confirmar. Ela trabalha em uma transação, mantém uma loja e sua organização, preserva cadastros compartilhados necessários, mantém o catálogo CMED, limpa sessões e credenciais e transforma transferências entre lojas em histórico com snapshot.

O resultado deve informar uma loja e uma organização.

### 9.5 Arquivar mídias

Com internet disponível:

```powershell
& $ffMigration archive-media `
  --host 127.0.0.1 `
  --port 54329 `
  --database $ffStaging `
  --username farmaflow
```

Consulte os arquivos ausentes:

```powershell
$env:PGPASSWORD = $ffDbPassword
& "$ffPgBin\psql.exe" `
  --host 127.0.0.1 `
  --port 54329 `
  --username farmaflow `
  --dbname $ffStaging `
  --command "select media_id, source_url, failure from public.local_media_blobs where missing order by media_id"
Remove-Item Env:\PGPASSWORD
```

Registre cada mídia ausente. A ausência de mídia não deve ser escondida na decisão go/no-go.

### 9.6 Exportar o pacote isolado

```powershell
$ffStorePackage = "D:\FarmaFlow-Corte\loja-1.ffbackup"
& $ffMigration export-full `
  --host 127.0.0.1 `
  --port 54329 `
  --database $ffStaging `
  --username farmaflow `
  --pg-bin $ffPgBin `
  --ssl-mode Prefer `
  --store-id $ffStoreId `
  --output $ffStorePackage

& $ffMigration verify --input $ffStorePackage
```

Confira no manifesto `<pacote>.json`:

- `kind` igual a `STORE`;
- `storeId` igual à loja correta;
- `organizationId` correto;
- `databaseMajorVersion` igual a `17`;
- `schemaVersion` igual ou superior a `52`;
- `packageSha256` correspondente ao arquivo.

Copie o `.ffbackup` e seu `.json` para dois locais protegidos.

### 9.7 Repetir para a próxima loja

Crie outro banco de staging a partir do arquivo integral original. Nunca tente transformar o staging já filtrado da primeira loja no pacote da segunda.

Depois de validar e copiar os dois pacotes, remova somente os bancos de staging identificados no registro do corte:

```powershell
$env:PGPASSWORD = $ffDbPassword
& "$ffPgBin\dropdb.exe" `
  --host 127.0.0.1 `
  --port 54329 `
  --username farmaflow `
  $ffStaging
Remove-Item Env:\PGPASSWORD
$ffDbPassword = $null
$ffSecrets = $null
```

## 10. Executar o corte real

### 10.1 Antes da manutenção

- confirme que o ensaio foi aprovado;
- confirme que todos os caixas podem ser fechados;
- copie os instaladores e a ferramenta de migração para os servidores;
- confirme nobreak, disco, rede privada e destino externo de backup;
- avise o horário de indisponibilidade;
- deixe o backend cloud disponível até iniciar formalmente a manutenção.

### 10.2 Fechar o ambiente cloud

1. Feche todos os caixas.
2. Encerre as sessões operacionais.
3. Coloque o backend cloud em manutenção ou pare sua implantação.
4. Confirme que as APIs de gravação não estão disponíveis.
5. Registre o horário do bloqueio.
6. Não inicie o FarmaFlow local ainda.

O mecanismo de manutenção depende da plataforma onde o backend cloud está hospedado; não existe um comando genérico no instalador local.

### 10.3 Gerar os pacotes finais

Repita as etapas 7 e 9 usando um novo nome, por exemplo `farmaflow-integral-corte.ffbackup`. Não reutilize os pacotes do ensaio: eles não contêm as operações feitas depois daquele snapshot.

Gere e valide os dois pacotes finais antes de abrir qualquer loja no ambiente local.

## 11. Instalar o servidor da loja

### 11.1 Executar o instalador

1. Entre no Windows com uma conta administradora.
2. Confira novamente o hash e a assinatura de `FarmaFlow-Server-Setup.exe`.
3. Execute o instalador como administrador.
4. Mantenha o diretório padrão `C:\Program Files\FarmaFlow Server`, salvo necessidade documentada.
5. Aguarde a inicialização do PostgreSQL.

O instalador:

- cria `%ProgramData%\FarmaFlow\Server`;
- gera senhas e chaves aleatórias em `secrets.json`;
- inicializa PostgreSQL em `127.0.0.1:54329`;
- cria os serviços `FarmaFlowPostgreSQL` e `FarmaFlowServer`;
- abre somente TCP `8443` no perfil de rede privada;
- mantém `FarmaFlowServer` parado enquanto não houver exatamente uma loja e schema V52 ou superior.

Confira:

```powershell
Get-Service FarmaFlowPostgreSQL
Get-Service FarmaFlowServer
Get-NetFirewallRule -DisplayName "FarmaFlow Server HTTPS"
```

Neste momento, PostgreSQL deve estar ativo e o Host pode estar parado aguardando a migração.

### 11.2 Restaurar o pacote da loja

Extraia `FarmaFlow-Migration.zip` e abra PowerShell como administrador:

```powershell
$ffInstall = "C:\Program Files\FarmaFlow Server"
$ffPgBin = Join-Path $ffInstall "runtime\postgres\bin"
$ffMigration = "C:\FarmaFlow\Migration\FarmaFlow.Migration.exe"
$ffStorePackage = "D:\FarmaFlow-Corte\loja-1.ffbackup"

& $ffMigration verify --input $ffStorePackage

& $ffMigration restore `
  --input $ffStorePackage `
  --host 127.0.0.1 `
  --port 54329 `
  --database farmaflow `
  --username farmaflow `
  --pg-bin $ffPgBin
```

Mantenha `<pacote>.json` ao lado do pacote. Sem o manifesto, a ferramenta ainda valida AES-GCM e o catálogo, mas não consegue comparar automaticamente todas as contagens e reconciliações da origem.

O resultado esperado termina com `Restauração concluída e validada`.

### 11.3 Configurar a cópia externa de backup

Antes de ativar o Host, edite como administrador:

`C:\Program Files\FarmaFlow Server\appsettings.json`

Preserve os valores existentes. Dentro de `ServerHost`, substitua apenas esta
linha:

```json
"BackupTime": "02:00",
```

por estas duas linhas:

```json
"BackupTime": "02:00",
"ExternalBackupDirectory": "E:\\FarmaFlow-Backups",
```

Use uma pasta que continue disponível no horário configurado. Uma atualização do instalador pode substituir `appsettings.json`; confira essa configuração depois de cada atualização.

### 11.4 Ativar o servidor

No menu Iniciar, execute **Ativar FarmaFlow após migração**. Como alternativa:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\FarmaFlow Server\installer\activate-server.ps1" `
  -InstallDirectory "C:\Program Files\FarmaFlow Server"
```

O script exige schema V52 ou superior e exatamente uma loja. Depois da ativação:

```powershell
Get-Service FarmaFlowPostgreSQL
Get-Service FarmaFlowServer
```

Os dois serviços devem estar em execução. O Host inicia Spring e Next.js; o Flyway aplica migrations posteriores presentes na release.

### 11.5 Registrar o certificado

Depois que o Host iniciar, abra no menu Iniciar **Certificado do FarmaFlow Server** ou leia:

`%ProgramData%\FarmaFlow\Server\certificate.sha256.txt`

Registre:

- nome do servidor;
- SHA-256 com 64 caracteres;
- endereço utilizado pelas estações, preferencialmente `https://NOME-DO-SERVIDOR:8443`.

Transfira a impressão digital às estações por um canal confiável. Não envie o arquivo `secrets.json`.

## 12. Validar a implantação do servidor

Em outro computador da mesma rede, use `Test-LocalDeployment.ps1`, incluído em `FarmaFlow-Migration.zip`:

```powershell
.\Test-LocalDeployment.ps1 `
  -Server "NOME-DO-SERVIDOR" `
  -Port 8443 `
  -CertificateSha256 "SHA256_SEM_ESPACOS"
```

O resultado deve conter:

```json
{
  "status": "PASS",
  "deploymentMode": "LOCAL_SINGLE_STORE",
  "publicPort": 8443
}
```

O teste também confirma que `54329`, `8180` e `3100` não estão expostas.

Faça ainda estas verificações:

- desligue temporariamente a internet, mas mantenha a rede local;
- abra o FarmaFlow pelo endereço HTTPS;
- faça login com uma conta migrada;
- confirme a loja correta em **Configurações > Status**;
- confirme aviso de indisponibilidade nas integrações que exigem internet;
- confirme que Meta está desconectado no modo local.

## 13. Instalar e parear uma estação

### 13.1 Instalar

1. Entre no Windows com o usuário que usará a estação.
2. Confira hash e assinatura de `FarmaFlow-Estacao-Setup.exe`.
3. Execute o instalador.
4. Se desejar, selecione o atalho na área de trabalho.
5. Aguarde a verificação do WebView2.

O programa inicia com o Windows para aquele usuário.

### 13.2 Configurar o servidor

Na primeira abertura, informe:

- **Endereço HTTPS do servidor:** `https://NOME-DO-SERVIDOR:8443`;
- **Impressão digital SHA-256:** os 64 caracteres registrados no servidor.

Compare a impressão digital por um canal confiável antes de selecionar **Salvar**. Se o certificado mudar inesperadamente, pare a operação e investigue; não substitua a impressão digital apenas para remover o erro.

### 13.3 Fazer login e parear

1. Faça login com o usuário migrado. As sessões antigas foram invalidadas; todos devem entrar novamente.
2. Abra **Configurações > Estações**.
3. Crie a estação e copie o código exibido.
4. O código expira em 10 minutos e pode ser usado uma vez.
5. No ícone do FarmaFlow na bandeja do Windows, selecione **Parear estação**.
6. Informe o código.
7. Abra **Status** no menu da bandeja e confirme `Agente conectado`.

Repita em cada estação da loja. Um pacote e um servidor de uma loja não podem ser usados pela outra.

## 14. Checklist funcional antes do go-live

Execute em cada loja:

- login de todas as contas relevantes;
- abertura, movimentação e fechamento de caixa;
- venda com cada forma de pagamento utilizada;
- cancelamento autorizado;
- atualização de estoque e lote;
- compra e nota de entrada;
- inventário e divergências;
- clientes, entregas e encomendas;
- impressão de teste, comprovante e documento PDF;
- concorrência com duas ou mais estações;
- reinício do servidor e reconexão das estações;
- operação com internet indisponível;
- bloqueio ao usar uma impressão digital de certificado incorreta.

Compare com o manifesto e o relatório do corte:

- contagens por tabela;
- vendas e pagamentos por dia;
- estoque por produto e lote;
- saldos e movimentos de caixa;
- compras e notas;
- inventários;
- sequências;
- mídias ausentes;
- exatamente uma loja e uma organização no banco local.

## 15. Decisão go/no-go

### Go

Somente declare **go** quando:

- os pacotes e manifestos estiverem íntegros;
- a reconciliação automática tiver terminado sem divergências;
- a conferência manual estiver aprovada;
- o servidor e ao menos uma estação estiverem operacionais sem internet;
- impressão, venda, estoque e caixa estiverem aprovados;
- backup e chave de recuperação estiverem configurados;
- somente a porta `8443` estiver acessível pela rede.

Registre o horário da primeira venda local. A partir desse momento, o Supabase deixa de ser um destino de rollback automático.

### No-go antes da primeira gravação local

1. Mantenha o serviço `FarmaFlowServer` parado.
2. Registre o motivo.
3. Confirme que nenhuma venda foi gravada localmente.
4. Reabra o backend cloud.
5. Confirme a operação cloud antes de liberar as estações.

### Falha depois da primeira gravação local

Não reabra o Supabase. Restaure o servidor local, corrija a falha ou opere com o procedimento de contingência da loja. Reabrir o cloud criaria duas fontes divergentes e exigiria uma reconciliação não automatizada.

## 16. Backups locais

O Host cria backups criptografados diariamente, por padrão às `02:00`, em:

`%ProgramData%\FarmaFlow\Server\backups`

Quando `ExternalBackupDirectory` está configurado, ele copia o `.ffbackup` e o manifesto `.json` para o destino externo.

A retenção implementada é:

- 14 backups diários;
- 8 semanais;
- 12 mensais.

### 16.1 Exportar a chave de recuperação

Abra PowerShell como administrador:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\FarmaFlow Server\installer\export-recovery-key.ps1" `
  -Destination "E:\FarmaFlow-Segredos\recovery-key-loja-1.txt"
```

Guarde esse arquivo fora do servidor, de preferência em mídia criptografada e com acesso restrito. Não o mantenha na mesma pasta dos backups.

### 16.2 Verificar um backup diário

Antes da restauração, compare o SHA-256 com o manifesto:

```powershell
$ffBackup = "E:\FarmaFlow-Backups\farmaflow-AAAAmmdd-HHMMSS.ffbackup"
$ffManifest = Get-Content "$ffBackup.json" -Raw | ConvertFrom-Json
$ffActualHash = (Get-FileHash $ffBackup -Algorithm SHA256).Hash
if ($ffActualHash -ne $ffManifest.sha256) {
  throw "Checksum do backup divergente."
}
```

### 16.3 Fazer uma restauração de prova

Crie um banco descartável e restaure nele. Não execute o teste sobre o banco de produção.

```powershell
$ffRestoreDatabase = "farmaflow_restore_test"
$ffSecrets = Get-Content "$env:ProgramData\FarmaFlow\Server\secrets.json" -Raw | ConvertFrom-Json
$ffDbPassword = [string]$ffSecrets.DatabasePassword
$ffPgBin = "C:\Program Files\FarmaFlow Server\runtime\postgres\bin"
$ffMigration = "C:\FarmaFlow\Migration\FarmaFlow.Migration.exe"

$env:PGPASSWORD = $ffDbPassword
& "$ffPgBin\createdb.exe" --host 127.0.0.1 --port 54329 --username farmaflow $ffRestoreDatabase
Remove-Item Env:\PGPASSWORD

& $ffMigration restore-server-backup `
  --input $ffBackup `
  --host 127.0.0.1 `
  --port 54329 `
  --database $ffRestoreDatabase `
  --username farmaflow `
  --pg-bin $ffPgBin
```

Quando solicitado, informe somente o valor Base64 de `BackupKey` presente no arquivo exportado e depois a senha do PostgreSQL local. O resultado deve confirmar schema V52 ou superior.

Depois de registrar o teste, remova apenas o banco descartável:

```powershell
$env:PGPASSWORD = $ffDbPassword
& "$ffPgBin\dropdb.exe" --host 127.0.0.1 --port 54329 --username farmaflow $ffRestoreDatabase
Remove-Item Env:\PGPASSWORD
$ffDbPassword = $null
$ffSecrets = $null
```

Faça uma restauração de prova no ensaio, após o primeiro backup de produção e periodicamente durante os 90 dias.

## 17. Supabase depois do corte

1. Mantenha o backend cloud parado.
2. Não direcione estações ou integrações ao Supabase.
3. Mantenha o projeto ativo, sem gravações, por 90 dias.
4. Guarde o arquivo integral final e a preservação lógica adicional fora dos servidores das lojas.
5. Monitore acessos inesperados e conexões de aplicações antigas.
6. Faça ao menos uma restauração de prova durante a retenção.
7. No final dos 90 dias, faça uma nova restauração completa e reconcilie o resultado.
8. Exija autorização humana explícita antes de pausar ou excluir o projeto.

O Supabase é arquivo histórico durante esse período, não failover automático.

## 18. Atualização

Esta versão ainda não possui atualização automática operacional. Atualize somente com um instalador aprovado:

1. confirme que não há caixa aberto;
2. gere e valide um backup local;
3. registre a versão atual;
4. confira assinatura e hash do novo instalador;
5. execute a atualização no servidor;
6. confira `ExternalBackupDirectory` em `appsettings.json`;
7. confirme Flyway, login, venda, estoque, caixa e impressão;
8. mantenha o backup anterior até concluir a validação.

## 19. Desinstalação

A desinstalação remove os serviços e a regra de firewall, mas preserva por padrão:

- banco PostgreSQL;
- `secrets.json`;
- certificado e impressão digital;
- backups;
- demais dados em `%ProgramData%\FarmaFlow\Server`.

Não apague essa pasta sem uma solicitação explícita, backup validado e autorização registrada.

A desinstalação da estação remove o programa e a inicialização automática daquele usuário. Antes de reutilizar o computador para outra loja, remova ou substitua conscientemente a configuração em `%LocalAppData%\FarmaFlow\Agent`.

## 20. Solução de problemas

### O Host não inicia

```powershell
Get-Service FarmaFlowPostgreSQL
Get-Service FarmaFlowServer
Get-Content "$env:ProgramData\FarmaFlow\Server\migration-required.txt" -ErrorAction SilentlyContinue
```

Confirme que a restauração terminou, o schema é V52 ou superior e existe exatamente uma loja. Execute novamente **Ativar FarmaFlow após migração** somente depois de corrigir a causa.

### A estação não abre o servidor

- teste resolução do nome do servidor;
- teste `Test-NetConnection NOME-DO-SERVIDOR -Port 8443`;
- confirme que a rede do servidor está como Privada;
- confira a regra `FarmaFlow Server HTTPS`;
- compare novamente os 64 caracteres da impressão digital;
- não desative a validação do certificado.

### O pacote é recusado

- mantenha o `.ffbackup` e o `.json` juntos;
- confira o SHA-256;
- confirme a senha correta;
- confirme que o arquivo não foi alterado;
- execute `FarmaFlow.Migration verify` antes de tentar restaurar novamente.

### O filtro recusa o banco

O nome deve conter `staging`. Use um banco descartável criado especificamente para a loja e digite o nome exato quando solicitado.

### Existem mídias ausentes

Consulte `public.local_media_blobs` no staging, registre `source_url` e `failure` e decida se a loja pode operar sem cada arquivo. Não substitua o problema por uma URL privada ou não validada.

### Integrações externas estão indisponíveis

No modo local, CNPJ, CEP, Cosmos e atualização CMED dependem de internet. Meta permanece desconectado por padrão porque suas credenciais são removidas do pacote da loja.

## 21. Checklist final

- [ ] Release e três commits registrados.
- [ ] Hashes e assinaturas conferidos.
- [ ] Ensaio completo aprovado.
- [ ] Backup integral final e preservação adicional criados.
- [ ] Data API endurecido e backend JDBC testado.
- [ ] Pacote de cada loja validado e armazenado em dois locais.
- [ ] Backend cloud parado antes da restauração final.
- [ ] Um servidor instalado e isolado por loja.
- [ ] Exatamente uma loja e uma organização em cada servidor.
- [ ] Somente TCP `8443` exposta na rede privada.
- [ ] Certificados e fingerprints registrados.
- [ ] Estações instaladas, configuradas e pareadas.
- [ ] Sete usuários orientados a fazer novo login.
- [ ] Vendas, pagamentos, estoque, caixa, compras, inventários, entregas e impressão testados.
- [ ] Backup local, cópia externa e chave de recuperação validados.
- [ ] Restauração de prova concluída.
- [ ] Decisão go/no-go registrada.
- [ ] Horário da primeira venda local registrado.
- [ ] Supabase congelado por 90 dias, sem pausa ou exclusão automática.
