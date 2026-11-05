================================================================================
CLAUDE.md — BOT DE WHATSAPP (produto separado do Core)
Arquivo de contexto para o agente Claude Code
================================================================================

Este repositório é o **Bot de atendimento via WhatsApp** do sistema de gestão para escritórios
de advocacia. É um produto **separado** do Core (`MiguelFariasDev/Iustitia`): repositório próprio,
banco próprio, deploy próprio. Os dois se falam **somente por API REST**.

Leia também o ADR-067 do Core (`docs/adr/067-modulo-atendimento-inbox-e-handoff.md`): ele é a
fonte das decisões de handoff. Este arquivo cobre o que é específico do Bot.

--------------------------------------------------------------------------------
1. RESPONSABILIDADES
--------------------------------------------------------------------------------

O Bot FAZ:
- Receber webhooks da **WhatsApp Cloud API oficial** da Meta e **validar a assinatura**.
- Conduzir o fluxo automático (menu) com o cliente final.
- Registrar a conversa no Core e **respeitar o status que o Core informa**.
- Entregar à Meta as mensagens que o advogado escreve no Core (`POST /api/v1/bot/send`).

O Bot NÃO FAZ:
- Acessar o banco do Core. Nunca. Tudo vem pela API.
- Decidir quem conduz a conversa. O estado (Bot / PendingHuman / Human / Closed) é do Core.
- Usar qualquer API **não oficial** do WhatsApp.

--------------------------------------------------------------------------------
2. REGRAS INEGOCIÁVEIS
--------------------------------------------------------------------------------

1. **Nunca enviar mensagem automática se o status for `Human`** — e, em `PendingHuman`, só o
   aviso de espera do handoff. O Bot consulta o status ANTES de responder e registra a resposta
   no Core ANTES de enviá-la à Meta, para o Core poder vetá-la (`ATENDIMENTO_BOT_BLOCKED`).
2. **Sempre validar `X-Hub-Signature-256`** sobre o corpo BRUTO, em tempo constante, antes de
   interpretar o payload. App Secret ausente rejeita TUDO (falha fechada), e o Bot nem sobe.
3. **Banco do Bot isolado do banco do Core.** O banco do Bot guarda só o livro de idempotência.
4. **Nunca logar telefone nem texto de mensagem** (dado pessoal e sigilo profissional). Só ids
   técnicos (`wamid`, `messageId`) e códigos de erro.
5. **Nunca commitar segredos** (App Secret, tokens da Meta, chaves de API). Em produção, Key Vault.
6. **Transparência com o cliente final:** o primeiro contato informa que o atendimento é
   automatizado e que ele pode falar com uma pessoa (Recomendação nº 001/2024 da OAB).
7. **Marca Desacoplada:** namespaces, projetos e banco são genéricos (`Advocacia.Bot.*`,
   `advocacia_bot_db`). A marca só aparece em documentação e configuração.

--------------------------------------------------------------------------------
3. STACK
--------------------------------------------------------------------------------

.NET 10 · ASP.NET Core Minimal API · EF Core 10 + Npgsql (PostgreSQL 16) · Refit 8 (cliente do
Core) · Microsoft.Extensions.Http.Resilience (Polly) · xUnit + FluentAssertions 6 + NSubstitute ·
Testcontainers (PostgreSQL). Versões fixadas em `Directory.Packages.props`, alinhadas com o Core.

--------------------------------------------------------------------------------
4. ESTRUTURA
--------------------------------------------------------------------------------

  src/Bot.Application/    Fluxo e máquina de estados (sem I/O): ConversationService,
                          ConversationFlow, OutboundSendService, validação de assinatura,
                          parser do payload da Meta, portas (ICoreApiClient, IWhatsAppClient…).
  src/Bot.Infrastructure/ Refit para o Core, HttpClient para a Meta, EF Core (livro de
                          idempotência), configuração. Aqui mora todo I/O.
  src/Bot.Api/            Host: /webhooks/whatsapp (GET verificação, POST eventos),
                          /api/v1/bot/send, health checks, validação de configuração na subida.
  tests/Bot.UnitTests/         Sem rede nem banco.
  tests/Bot.IntegrationTests/  Host real + PostgreSQL real; Core e Meta são servidores falsos.
  docs/                   Contrato com o Core, configuração, operação.

Regra de dependência: Application não conhece Infrastructure. Application não faz I/O.

--------------------------------------------------------------------------------
5. CONVENÇÕES (mesmas do Core)
--------------------------------------------------------------------------------

- Namespaces `Advocacia.Bot.<Camada>`; file-scoped namespaces; nullable habilitado.
- Async sempre com `CancellationToken`; nunca `.Result`/`.Wait()`/`async void`.
- Result pattern (`Result`/`Result<T>`/`Error`) em vez de exceções para falhas esperadas.
  `Error.IsTransient` decide se o webhook responde 500 (a Meta reentrega) ou 200.
- Sem `#region`. Comentário só quando o "porquê" não é óbvio.
- Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`).
- Branches `feature/*`; nunca commit direto em `main`/`develop`.

--------------------------------------------------------------------------------
6. COMANDOS
--------------------------------------------------------------------------------

  dotnet restore && dotnet build
  dotnet test tests/Bot.UnitTests
  dotnet test tests/Bot.IntegrationTests        # sobe PostgreSQL via Testcontainers

  # Sem Docker: aponte para um PostgreSQL 16 já em pé (superusuário). A fixture cria e
  # remove um banco descartável nele.
  ADVOCACIA_TEST_PG_SERVER="Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres" \
    dotnet test tests/Bot.IntegrationTests

  dotnet ef migrations add <Nome> --project src/Bot.Infrastructure --startup-project src/Bot.Api \
    --context BotDbContext --output-dir Persistence/Migrations
  dotnet ef database update --project src/Bot.Infrastructure --startup-project src/Bot.Api

--------------------------------------------------------------------------------
7. O QUE NUNCA FAZER
--------------------------------------------------------------------------------

X Responder em `Human`.
X Aceitar webhook sem assinatura válida.
X Logar telefone ou corpo de mensagem.
X Acessar o banco do Core ou duplicar regra de negócio do Core.
X Retry automático no envio à Meta (duplicaria a mensagem do cliente).
X Reutilizar a chave que o Bot usa para chamar o Core como a chave que o Core usa para chamar o Bot.

================================================================================
