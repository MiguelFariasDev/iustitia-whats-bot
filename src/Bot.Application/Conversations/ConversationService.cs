using Advocacia.Bot.Application.Abstractions;
using Advocacia.Bot.Application.Webhooks;
using Microsoft.Extensions.Logging;

namespace Advocacia.Bot.Application.Conversations;

public enum InboundOutcome
{
    /// <summary>Processada; o bot respondeu ao cliente.</summary>
    Replied,

    /// <summary>Processada; o bot ficou em silêncio porque há um humano (ou fila de humano) na conversa.</summary>
    Silent,

    /// <summary>O pedido de handoff foi feito e o cliente foi avisado.</summary>
    HandedOff,

    /// <summary>Reentrega de uma mensagem que já tratamos (ou que está sendo tratada agora).</summary>
    Duplicate,

    /// <summary>Número desconhecido: não há escritório configurado para ele.</summary>
    UnknownChannel,
}

/// <summary>
/// A máquina de estados do Bot. O ESTADO da conversa é do Core — o Bot só o consulta e o
/// respeita —, e é isso que garante a regra inegociável: nenhuma mensagem automática sai
/// enquanto o atendimento estiver com um humano.
///
/// Roteiro de cada mensagem recebida:
/// <code>
///  1. reivindica o wamid (idempotência)         6. Human/PendingHuman → silêncio
///  2. abre/recupera o atendimento no Core       7. "2" → pede handoff, avisa o cliente
///  3. registra a mensagem do cliente no Core    8. senão → resposta do menu
///  4. consulta o status (fonte da verdade)      9. conclui o wamid
/// </code>
/// Registrar a resposta do bot no Core ANTES de enviá-la à Meta é o que fecha a corrida em que
/// o advogado assume entre o passo 4 e o envio: o Core recusa a fala (<c>ATENDIMENTO_BOT_BLOCKED</c>)
/// e nada é enviado.
/// </summary>
public sealed class ConversationService(
    IChannelRegistry channels,
    ICoreApiClient core,
    IWhatsAppClient whatsApp,
    IIdempotencyLedger ledger,
    ILogger<ConversationService> logger)
{
    public async Task<Result<InboundOutcome>> HandleAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        var channel = channels.FindByPhoneNumberId(message.PhoneNumberId);
        if (channel is null)
        {
            // Nunca loga o telefone do cliente.
            logger.LogWarning("Mensagem para o número {PhoneNumberId}, que não está configurado.", message.PhoneNumberId);
            return Result.Success(InboundOutcome.UnknownChannel);
        }

        var key = $"in:{message.WhatsAppMessageId}";
        var claim = await ledger.ClaimAsync(key, cancellationToken);
        if (claim.Outcome != ClaimOutcome.Claimed)
        {
            return Result.Success(InboundOutcome.Duplicate);
        }

        Result<InboundOutcome> result;
        try
        {
            result = await ProcessAsync(channel, message, cancellationToken);
        }
        catch
        {
            await ledger.ReleaseAsync(key, CancellationToken.None);
            throw;
        }

        if (result.IsSuccess)
        {
            await ledger.CompleteAsync(key, result.Value.ToString(), cancellationToken);
        }
        else
        {
            // Falha transitória: libera para a reentrega da Meta tentar de novo. Falha permanente
            // também libera — o webhook decide, pelo tipo do erro, se pede reentrega.
            await ledger.ReleaseAsync(key, CancellationToken.None);
        }

        return result;
    }

    private async Task<Result<InboundOutcome>> ProcessAsync(
        WhatsAppChannel channel, InboundMessage message, CancellationToken cancellationToken)
    {
        var started = await core.StartAtendimentoAsync(channel, message.From, message.ContactName, cancellationToken);
        if (started.IsFailure)
        {
            return Result.Failure<InboundOutcome>(started.Error);
        }

        var atendimentoId = started.Value.AtendimentoId;

        // A mensagem do cliente é SEMPRE registrada, inclusive com um humano na conversa — é o
        // que o advogado vai ler. Mídia entra como marcador, para o histórico não ter buracos.
        var body = message.Text ?? $"[mensagem do tipo '{message.Type}' — conteúdo não suportado pelo bot]";
        var recorded = await core.SendMessageAsync(
            channel, atendimentoId, MessageDirection.Inbound, MessageSender.Client, body,
            message.WhatsAppMessageId, cancellationToken);
        if (recorded.IsFailure)
        {
            return Result.Failure<InboundOutcome>(recorded.Error);
        }

        // O status vindo do "start" pode já estar velho; este é o que vale.
        var status = await core.GetStatusAsync(channel, atendimentoId, cancellationToken);
        if (status.IsFailure)
        {
            return Result.Failure<InboundOutcome>(status.Error);
        }

        if (status.Value != AtendimentoStatuses.Bot)
        {
            return Result.Success(InboundOutcome.Silent);
        }

        if (!message.IsText)
        {
            return await ReplyAsync(
                channel, message, atendimentoId, ConversationFlow.MediaNotSupportedNotice,
                InboundOutcome.Replied, cancellationToken);
        }

        var decision = ConversationFlow.Decide(message.Text, started.Value.Created, channel.OfficeName);

        if (decision is FlowDecision.Handoff)
        {
            var handoff = await core.RequestHandoffAsync(channel, atendimentoId, cancellationToken);
            if (handoff.IsFailure)
            {
                return Result.Failure<InboundOutcome>(handoff.Error);
            }

            return await ReplyAsync(
                channel, message, atendimentoId, ConversationFlow.HandoffNotice,
                InboundOutcome.HandedOff, cancellationToken);
        }

        return await ReplyAsync(
            channel, message, atendimentoId, ((FlowDecision.Reply)decision).Text,
            InboundOutcome.Replied, cancellationToken);
    }

    private async Task<Result<InboundOutcome>> ReplyAsync(
        WhatsAppChannel channel,
        InboundMessage message,
        Guid atendimentoId,
        string text,
        InboundOutcome successOutcome,
        CancellationToken cancellationToken)
    {
        // Determinístico: se a Meta reentregar, o Core devolve o mesmo registro em vez de duplicar.
        var recorded = await core.SendMessageAsync(
            channel, atendimentoId, MessageDirection.Outbound, MessageSender.Bot, text,
            $"bot:{message.WhatsAppMessageId}", cancellationToken);

        if (recorded.IsFailure)
        {
            // O advogado assumiu entre a consulta e agora: silêncio, e não é erro.
            if (recorded.Error.Code == CoreErrorCodes.BotBlocked)
            {
                return Result.Success(InboundOutcome.Silent);
            }

            return Result.Failure<InboundOutcome>(recorded.Error);
        }

        var sent = await whatsApp.SendTextAsync(channel, message.From, text, cancellationToken);
        return sent.IsFailure
            ? Result.Failure<InboundOutcome>(sent.Error)
            : Result.Success(successOutcome);
    }
}
