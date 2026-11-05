# Contrato HTTP com o Core

A fonte de verdade é o Core (`docs/modules/atendimento.md`, ADR-067 e ADR-068 no repositório do Core). Este
documento resume o que o Bot usa. Os dois lados têm testes que fixam estes formatos
(`CoreApiClientTests` aqui; `AtendimentoHandoffFlowTests` no Core).

## Bot → Core (`X-Api-Key` do escritório; o tenant vem da chave)

| Chamada | Corpo | Resposta |
|---|---|---|
| `POST /api/v1/bot/atendimentos` | `{ phoneNumber, contactName? }` | `200 { atendimentoId, status, created }` — idempotente |
| `POST /api/v1/bot/atendimentos/{id}/mensagens` | `{ direction, sender, body, externalId? }` | `200 { messageId }` — idempotente por `externalId` |
| `POST /api/v1/bot/atendimentos/{id}/handoff` | — | `200 { success: true }` — idempotente |
| `GET  /api/v1/bot/atendimentos/{id}/status` | — | `200 { atendimentoId, status }` |

`direction`: `Inbound` \| `Outbound`. `sender`: `Client` \| `Bot`. `status`: `Bot` \| `PendingHuman` \| `Human` \| `Closed`.

**`externalId` das mensagens**
- Entrada do cliente: o `wamid` da Meta.
- Resposta do bot: `bot:{wamid da mensagem que a originou}` — determinístico, para uma reentrega
  do webhook não duplicar a resposta no histórico do Core.

**Erros** (ProblemDetails com `errorCode`):

| HTTP | `errorCode` | Como o Bot trata |
|---|---|---|
| 409 | `ATENDIMENTO_BOT_BLOCKED` | O advogado assumiu entre a consulta e a resposta: **silêncio**, não é erro |
| 401 / 403 | — | Chave revogada/expirada: **transitório** (a Meta reentrega) + alerta de operação |
| 429 / 5xx / rede | — | Transitório: o webhook responde 500 e a Meta reentrega |
| outros 4xx | variados | Permanente: loga o código, responde 200 |

## Consulta de andamento (Bot → Core, mesma `X-Api-Key`)

A identidade é verificada **no Core**. O Bot não compara documento, não guarda sessão e não vê o cadastro:
repassa o que o cliente digitou e age conforme a `etapa`. Base: `/api/v1/bot/atendimentos/{id}/consulta`.

| Chamada | Corpo | Resposta |
|---|---|---|
| `POST /iniciar` | — | `200 { etapa }` |
| `POST /verificar` | `{ documento }` (com ou sem pontuação) | `200 { etapa, processos?: [{ indice, titulo }] }` |
| `GET  /processos/{indice}` | — | `200 { titulo, fase, ultimoAndamento, atualizadoEm }` |

`etapa`: `AguardandoDocumento` \| `Verificado` \| `Negado` \| `Bloqueado` \| `Expirada`. `processos` vem só em `Verificado` (até 10).

| Etapa | O que o Bot faz |
|---|---|
| `AguardandoDocumento` | Pede o CPF/CNPJ (a sessão vale 10 min desde o `iniciar`) |
| `Verificado` | Lista os títulos; o cliente escolhe um `indice` e o Bot chama `GET processos/{indice}` |
| `Negado` | Mensagem genérica ("não conseguimos confirmar"). **Mesma resposta** para telefone sem cadastro e para documento errado — o Bot não deve sugerir qual dos dois foi |
| `Bloqueado` | Informa que a consulta ficou indisponível por um tempo (30 min) e oferece falar com uma pessoa |
| `Expirada` | Recomeça com `iniciar` |

**Erros específicos**

| HTTP | `errorCode` | Como o Bot trata |
|---|---|---|
| 409 | `ATENDIMENTO_CONSULTA_REQUER_BOT` | A conversa não é mais do bot (`PendingHuman`/`Human`/`Closed`): **silêncio**, como em `ATENDIMENTO_BOT_BLOCKED` |
| 403 | `ATENDIMENTO_CONSULTA_NAO_VERIFICADA` | Sessão expirada/não verificada: recomeçar com `iniciar` (não confundir com chave revogada) |
| 404 | `ATENDIMENTO_CONSULTA_PROCESSO_NOT_FOUND` | Índice fora da lista: reapresentar a lista |

> **Atenção:** o `403` desta rota é de regra de negócio e carrega `errorCode`; o `403` sem `errorCode`
> continua sendo chave revogada (transitório, tabela acima). O Bot deve distinguir pelo `errorCode`.

**Privacidade (o que o Bot deve fazer ao consumir)**

- **Nunca** logar o documento, o telefone nem o `ultimoAndamento`; só ids técnicos e `errorCode`.
- Ao registrar a mensagem do cliente que contém o documento (`POST .../mensagens`), enviar um marcador
  (ex.: `[mensagem omitida: verificação de identidade]`) em vez do texto. O Core também omite o corpo
  durante e até 60 s após a verificação, mas isso é só rede de segurança.
- Repassar `ultimoAndamento` ao cliente como veio; não complementar com outros dados.
- CPF + telefone é fator de identidade fraco (ver ADR-068); não ampliar o que é mostrado sem decisão do Core.

## Core → Bot (`X-Api-Key` do Core, `InboundApi:ApiKey`)

`POST /api/v1/bot/send`

```json
{ "tenantId": "…", "atendimentoId": "…", "messageId": "…", "to": "5585999998888", "body": "Bom dia" }
```

| Resposta | Significado |
|---|---|
| `200 { externalId }` | Enviada à Meta (`externalId` = `wamid`). Idempotente por `messageId`: repetir devolve o mesmo `wamid` sem reenviar |
| `400` | Pedido inválido (telefone, texto vazio ou > 4096) |
| `401` | Chave do Core ausente/errada |
| `404 BOT_UNKNOWN_TENANT` | Nenhum número configurado para o escritório |
| `409 BOT_SEND_IN_PROGRESS` | Outro envio da mesma mensagem em andamento |
| `502` | A Meta recusou ou está fora do ar (o Core marca a mensagem como `Failed`) |

O Core já grava a mensagem do advogado; o Bot **não** a registra de volta (duplicaria).
