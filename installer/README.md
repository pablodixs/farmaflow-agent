# Distribuição local do FarmaFlow

Os instaladores são deliberadamente separados:

- `FarmaFlow-Server-Setup.exe`: requer o diretório `publish-server` criado por `scripts/Stage-ServerPackage.ps1` e instala PostgreSQL 17, Java 21, Node 22, backend, frontend e o Host Windows.
- `FarmaFlow-Estacao-Setup.exe`: requer o diretório `publish` criado por `dotnet publish` e instala a estação WebView2/ impressão.
- `FarmaFlow.Migration.exe`: ferramenta administrativa separada; nunca é incluída nos instaladores operacionais.

O staging recusa runtimes incompletos. O workflow `.github/workflows/release.yml` prepara Java 21 com `jlink`, inclui Node 22 e usa o ZIP oficial do PostgreSQL 17.11 com SHA-256 fixado. O mesmo job compila backend, frontend e componentes Windows, evitando publicar instaladores formados por execuções diferentes.

O servidor expõe somente TCP 8443 no perfil de rede privada. PostgreSQL (54329), Spring (8180) e Next (3100) ficam em `127.0.0.1`. A desinstalação remove os serviços e preserva `%ProgramData%\FarmaFlow\Server`, incluindo banco, segredos e backups.

## GitHub Actions

O repositório `farmaflow-agent` é o agregador de release. Não é necessário mover os fontes para um monorepo: a action faz checkout dos repositórios `pablodixs/farmaflow.backend` e `pablodixs/farmaflow` e registra os commits resolvidos em `release-manifest.json`.

Para executar manualmente, abra **Actions > Build Windows installers > Run workflow** e informe a versão. Os campos `backend_ref` e `web_ref` aceitam branch, tag ou SHA. Também é possível definir `FARMAFLOW_BACKEND_REF` e `FARMAFLOW_WEB_REF` como variables do repositório; o padrão é `main`.

Se backend ou frontend forem privados, configure o secret `FARMAFLOW_REPOS_TOKEN` com um fine-grained personal access token que tenha apenas `Contents: read` nos dois repositórios. O `GITHUB_TOKEN` é suficiente quando eles forem públicos.

Para assinatura Authenticode opcional, configure:

- `WINDOWS_SIGNING_CERT_BASE64`: conteúdo Base64 do certificado PFX;
- `WINDOWS_SIGNING_CERT_PASSWORD`: senha do PFX.

Sem esses dois secrets, os instaladores continuam sendo gerados, mas ficam sem assinatura Authenticode. Em repositório público, a action também publica uma atestação de proveniência Sigstore. Tags no formato `vX.Y.Z` criam a GitHub Release; execuções manuais somente disponibilizam o bundle como artifact por 30 dias.

Cada bundle contém:

- `FarmaFlow-Server-Setup.exe`;
- `FarmaFlow-Estacao-Setup.exe`;
- `FarmaFlow-Migration.zip`;
- `release-manifest.json`, com commits, runtimes, tamanhos e hashes;
- `SHA256SUMS.txt`.
