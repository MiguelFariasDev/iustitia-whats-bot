# Bot de WhatsApp

Bot de atendimento via **WhatsApp Cloud API (oficial)** para o sistema de gestão de escritórios
de advocacia. Produto separado do Core; os dois se comunicam só por API REST.

```
 Cliente ──WhatsApp──▶ Meta ──webhook──▶ [ Bot ] ──REST──▶ [ Core ]  ◀── Advogado (Inbox)
                                           │  ▲                │
                                           │  └── /send ◀──────┘   (mensagem do advogado)
                                           └──▶ Meta ──▶ Cliente
```

## O que faz

1. Recebe o webhook da Meta e **valida a assinatura** (`X-Hub-Signature-256`).
2. Abre/recupera o atendimento no Core, registra a mensagem e **consulta o status**.
3. Em `Bot`: responde pelo menu automático. Se o cliente digita **2**, pede o *handoff* ao Core,
   avisa "Aguarde, um advogado vai te atender." e **para de responder**.
4. Em `PendingHuman`/`Human`: **fica em silêncio** — só registra o que o cliente escreve.
5. Entrega à Meta o que o advogado escreve no Core (`POST /api/v1/bot/send`).

## Rodando os testes

```bash
dotnet test tests/Bot.UnitTests
dotnet test tests/Bot.IntegrationTests   # PostgreSQL via Testcontainers (precisa de Docker)
```

Sem Docker, use um PostgreSQL já em pé: `ADVOCACIA_TEST_PG_SERVER="Host=…;Username=postgres;Password=…"`.

## Documentação

- [`docs/contrato-core.md`](docs/contrato-core.md) — o contrato HTTP com o Core, nos dois sentidos.
- [`docs/configuracao.md`](docs/configuracao.md) — configuração, segredos e como configurar a Meta.
- [`docs/operacao.md`](docs/operacao.md) — rotação de chaves, janela de 24 h, o que olhar em incidentes.
- [`CLAUDE.md`](CLAUDE.md) — regras e convenções do repositório.
