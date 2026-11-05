using Advocacia.Bot.Application.Outbound;

namespace Advocacia.Bot.Api;

/// <summary>
/// <c>POST /api/v1/bot/send</c> — o Core pede a entrega da mensagem que o advogado escreveu.
/// O Core nunca fala com a Meta; é aqui que a mensagem passa a ela.
/// </summary>
public static class SendEndpoints
{
    private const int MaxBodyLength = 4096;

    public static IEndpointRouteBuilder MapSendEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/bot/send", async (
                SendRequestBody body, OutboundSendService service, CancellationToken cancellationToken) =>
            {
                var to = new string((body.To ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

                if (body.TenantId == Guid.Empty || body.MessageId == Guid.Empty || to.Length is < 10 or > 15
                    || string.IsNullOrWhiteSpace(body.Body) || body.Body.Length > MaxBodyLength)
                {
                    return Problem(StatusCodes.Status400BadRequest, "BOT_INVALID_REQUEST", "Pedido de envio inválido.");
                }

                var result = await service.SendAsync(
                    new SendRequest(body.TenantId, body.AtendimentoId, body.MessageId, to, body.Body), cancellationToken);

                if (result.IsSuccess)
                {
                    return Results.Ok(new SendReply(result.Value));
                }

                var status = result.Error.Code switch
                {
                    OutboundErrorCodes.UnknownTenant => StatusCodes.Status404NotFound,
                    OutboundErrorCodes.InProgress => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status502BadGateway,
                };

                return Problem(status, result.Error.Code, result.Error.Message);
            })
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .WithName("SendMessage");

        return app;
    }

    private static IResult Problem(int status, string code, string message) =>
        Results.Problem(
            statusCode: status,
            title: code,
            detail: message,
            extensions: new Dictionary<string, object?> { ["errorCode"] = code });
}

/// <summary>Contrato com o Core. <c>MessageId</c> é a chave de idempotência.</summary>
public sealed record SendRequestBody(Guid TenantId, Guid AtendimentoId, Guid MessageId, string? To, string? Body);

public sealed record SendReply(string ExternalId);
