using Advocacia.Bot.Application.Abstractions;

namespace Advocacia.Bot.Application.Outbound;

/// <summary>Pedido do Core para entregar a mensagem do advogado ao cliente (<c>POST /api/v1/bot/send</c>).</summary>
public sealed record SendRequest(Guid TenantId, Guid AtendimentoId, Guid MessageId, string To, string Body);

public static class OutboundErrorCodes
{
    public const string UnknownTenant = "BOT_UNKNOWN_TENANT";
    public const string InProgress = "BOT_SEND_IN_PROGRESS";
}

/// <summary>
/// Entrega ao WhatsApp a mensagem que o advogado escreveu no Core. O Core nunca fala com a Meta;
/// é o Bot quem o faz. Idempotente por <c>MessageId</c>: se o Core repetir o pedido (timeout,
/// retry), o cliente NÃO recebe a mensagem duas vezes — devolve-se o mesmo <c>wamid</c>.
/// O Core já grava a mensagem; por isso o Bot não a registra de volta.
/// </summary>
public sealed class OutboundSendService(
    IChannelRegistry channels, IWhatsAppClient whatsApp, IIdempotencyLedger ledger)
{
    public async Task<Result<string>> SendAsync(SendRequest request, CancellationToken cancellationToken)
    {
        var channel = channels.FindByTenantId(request.TenantId);
        if (channel is null)
        {
            return Result.Failure<string>(Error.Permanent(
                OutboundErrorCodes.UnknownTenant, "Nenhum número de WhatsApp configurado para este escritório."));
        }

        var key = $"out:{request.MessageId}";
        var claim = await ledger.ClaimAsync(key, cancellationToken);

        switch (claim.Outcome)
        {
            case ClaimOutcome.AlreadyDone:
                return Result.Success(claim.Result ?? string.Empty);
            case ClaimOutcome.InProgress:
                return Result.Failure<string>(Error.Transient(
                    OutboundErrorCodes.InProgress, "Esta mensagem já está sendo enviada."));
        }

        Result<string> sent;
        try
        {
            sent = await whatsApp.SendTextAsync(channel, request.To, request.Body, cancellationToken);
        }
        catch
        {
            await ledger.ReleaseAsync(key, CancellationToken.None);
            throw;
        }

        if (sent.IsFailure)
        {
            await ledger.ReleaseAsync(key, CancellationToken.None);
            return sent;
        }

        await ledger.CompleteAsync(key, sent.Value, cancellationToken);
        return sent;
    }
}
