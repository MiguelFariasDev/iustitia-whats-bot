# Configuração

Segredos vêm de variáveis de ambiente / Key Vault — nunca do repositório. Em .NET, `:` vira `__`
em variável de ambiente (`WhatsApp__AppSecret`).

| Chave | Descrição |
|---|---|
| `ConnectionStrings:BotDb` | Banco **próprio** do Bot (`advocacia_bot_db`). Nunca o do Core |
| `Core:BaseUrl` | URL da API do Core |
| `InboundApi:ApiKey` | Chave que o **Core** apresenta ao Bot em `/api/v1/bot/send`. Gire periodicamente |
| `WhatsApp:AppSecret` | App Secret do app da Meta. Valida a assinatura de todo webhook |
| `WhatsApp:VerifyToken` | Token que você escolhe e cadastra no painel da Meta (verificação do webhook) |
| `WhatsApp:GraphApiVersion` | Padrão `v21.0` |
| `WhatsApp:Channels[]` | Um item por número/escritório (abaixo) |

Cada item de `WhatsApp:Channels`:

| Campo | Descrição |
|---|---|
| `TenantId` | Escritório no Core (o `tenantId` que o Core envia em `/send`) |
| `PhoneNumberId` | Id do número na Meta (`metadata.phone_number_id` do webhook) — **não** é o telefone |
| `AccessToken` | Token da Cloud API para este número |
| `CoreApiKey` | Chave emitida pelo dono do escritório no Core (`POST /api/v1/bot/api-keys`) |
| `OfficeName` | Nome usado nos textos do menu |

Sem `AppSecret`, `VerifyToken`, `Core:BaseUrl`, `InboundApi:ApiKey` ou canais completos, o Bot
**recusa subir** e lista os nomes (nunca os valores) que faltam.

## Configurando a Meta

1. Webhook: `https://<host-do-bot>/webhooks/whatsapp`, com o `WhatsApp:VerifyToken` acima.
2. Assine o campo `messages`.
3. Banco: `dotnet ef database update --project src/Bot.Infrastructure --startup-project src/Bot.Api`.
