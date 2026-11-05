# Operação

## Rotação de chaves

| Chave | Quem emite | Como girar |
|---|---|---|
| `Channels[].CoreApiKey` (Bot → Core) | Dono do escritório, no Core | Emitir nova (`POST /api/v1/bot/api-keys`), trocar no Bot, **revogar a antiga** (`DELETE`) |
| `InboundApi:ApiKey` (Core → Bot) | Operação | Trocar no Bot e no Core (`Atendimento:Bot:ApiKey`) na mesma janela |
| `AccessToken` / `AppSecret` | Painel da Meta | Conforme a política da Meta |

## Janela de 24 horas do WhatsApp

A Meta só aceita texto livre até 24 h depois da última mensagem do cliente. Fora da janela, o
envio é recusado (erro 131047) e a mensagem do advogado aparece como **Falhou** no Core. Responder
fora da janela exige template aprovado — fora do escopo atual.

## Por que o webhook às vezes responde 500

É proposital: falha **transitória** (Core ou Meta fora do ar, chave revogada) → 500 → a Meta
reentrega por até 7 dias, e o livro de idempotência impede resposta em duplicidade. Falha
**permanente** → 200 + log de erro, para a Meta não reentregar à toa.

## O que olhar num incidente

- `CORE_HTTP_401`/`403` repetidos: a chave do escritório foi revogada ou expirou.
- `META_HTTP_4xx`: token inválido, número fora da janela, ou número do cliente inválido.
- `BOT_UNKNOWN_TENANT`: falta o canal do escritório em `WhatsApp:Channels`.
- Registros de idempotência concluídos são apagados após 8 dias (a Meta reentrega por até 7).
