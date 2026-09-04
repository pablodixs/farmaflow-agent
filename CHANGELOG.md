# Changelog

## 0.1.8 — 2026-09-04

### Migração local

- Pacotes e backups novos usam envelope autenticado v3 em blocos, evitando manter dumps inteiros na memória.
- Restaurações usam uma única transação e validam PostgreSQL 17, schema V52–V54, Flyway, contagens, reconciliação, mídias e sequências.
- O filtro preserva modelos de etiqueta do sistema e remove referências CMED a usuários de outra organização.
- O arquivamento de mídias é repetível, preserva blobs válidos, bloqueia SSRF e falha claramente quando alguma mídia não pôde ser copiada.
- O instalador pode retomar uma instalação incompleta da mesma loja, mas bloqueia a sobrescrita de um servidor operacional.
- Cancelamento encerra processos filhos; temporários descriptografados ficam em pastas privadas e são removidos ao terminar.
- A CI executa exportação, filtro por loja, arquivamento, verificação, restauração e reconciliação contra PostgreSQL 17 real.
