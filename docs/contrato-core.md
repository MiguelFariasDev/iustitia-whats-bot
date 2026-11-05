# Contrato HTTP com o Core

A fonte de verdade é o Core (`docs/modules/atendimento.md` e ADR-067 no repositório do Core). Este
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
