# FarmaFlow local em 5 passos

Este é o caminho normal. O [guia técnico completo](GUIA-INSTALACAO-COMPLETA.md)
fica para o suporte quando algo sair do fluxo.

## 1. Suporte: preparar os pacotes

1. Execute `FarmaFlow-Migracao-Setup.exe`.
2. Escolha **Ensaio** na primeira execução.
3. Cole a conexão do PostgreSQL do Supabase e informe a senha. Se o project ref não puder ser identificado pelo pooler, informe também a URL `https://<project-ref>.supabase.co`.
4. Marque as lojas e escolha uma senha forte para o corte.
5. No ensaio, valide vendas, estoque, caixa e impressão. Para o corte, coloque o backend cloud em manutenção antes de escolher **Corte definitivo**.

O assistente cria um arquivo `.ffstore` por loja. Guarde os arquivos e a senha
em locais separados. O relatório sem credenciais fica na mesma pasta em HTML e
JSON.

## 2. Servidor: instalar uma loja

1. Execute `FarmaFlow-Server-Setup.exe` como administrador.
2. Selecione o `.ffstore` da loja e informe a senha do corte.
3. Escolha uma pasta externa para backups e outra mídia para a chave de recuperação.
4. Clique em **Instalar servidor** e aguarde a validação.

O banco, o backend, o frontend, o certificado, o firewall e o primeiro backup
são configurados automaticamente. O resultado inclui o kit da estação em
`%ProgramData%\FarmaFlow\Server\station-kit`.

## 3. Estação: conectar o computador

1. Copie `FarmaFlow-Estacao-Setup.exe` e o arquivo `.ffstation` do kit para o computador.
2. Execute o instalador.
3. Confirme a loja exibida. O instalador detecta automaticamente um único `.ffstation` colocado ao lado dele; se o arquivo foi entregue separadamente, abra-o depois da instalação.

Depois, abra o FarmaFlow, faça login e cadastre a estação. O código será enviado
automaticamente ao agente local. Se o agente não estiver disponível, use o
código de oito caracteres exibido na tela.

## 4. Liberar a operação

Confirme login, venda, pagamento, estoque, caixa e impressão sem internet.
Somente depois registre o **go** e o horário da primeira venda local.

## 5. Depois do corte

Mantenha o Supabase parado e sem gravações por 90 dias. Não abra o cloud para
rollback automático. Faça uma restauração de prova e guarde a chave de
recuperação fora do servidor.
