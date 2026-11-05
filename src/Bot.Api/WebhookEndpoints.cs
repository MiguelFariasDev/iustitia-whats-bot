using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Advocacia.Bot.Application.Conversations;
using Advocacia.Bot.Application.Webhooks;
using Advocacia.Bot.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Advocacia.Bot.Api;

/// <summary>
/// O webhook da WhatsApp Cloud API. Duas obrigações da Meta:
/// <list type="bullet">
/// <item><c>GET</c> de verificação, ao cadastrar o webhook: devolve o <c>hub.challenge</c> se o
/// <c>hub.verify_token</c> for o nosso;</item>
/// <item><c>POST</c> de eventos: responder 200 rápido. Qualquer outra resposta faz a Meta reentregar
/// (por até 7 dias) — usamos isso de propósito para falhas transitórias, e o livro de
/// idempotência garante que a reentrega não responde duas vezes ao cliente.</item>
/// </list>
/// </summary>
public static class WebhookEndpoints
{
    public const string Route = "/webhooks/whatsapp";

    private const int MaxBodyBytes = 1_048_576;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, (
                [FromQuery(Name = "hub.mode")] string? mode,
                [FromQuery(Name = "hub.verify_token")] string? token,
                [FromQuery(Name = "hub.challenge")] string? challenge,
                IOptionsMonitor<WhatsAppOptions> options) =>
            {
                var expected = options.CurrentValue.VerifyToken;

                var valid = mode == "subscribe"
                    && !string.IsNullOrWhiteSpace(expected)
                    && !string.IsNullOrEmpty(token)
                    && !string.IsNullOrEmpty(challenge)
                    && CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

                return valid ? Results.Text(challenge, "text/plain") : Results.StatusCode(StatusCodes.Status403Forbidden);
            })
            .WithName("VerifyWebhook")
            .ExcludeFromDescription();

        app.MapPost(Route, HandleEventsAsync)
            .WithName("ReceiveWebhook")
            .ExcludeFromDescription();

        return app;
    }

    private static async Task<IResult> HandleEventsAsync(
        HttpRequest request,
        IOptionsMonitor<WhatsAppOptions> options,
        ConversationService conversation,
        ILogger<ConversationService> logger,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // PRIMEIRA coisa: provar que veio da Meta, sobre os bytes brutos e antes de interpretar
        // qualquer coisa. Rejeitado aqui, o conteúdo nunca é processado nem logado.
        request.Headers.TryGetValue("X-Hub-Signature-256", out var signature);
        if (!MetaSignatureValidator.IsValid(body, signature.ToString(), options.CurrentValue.AppSecret))
        {
            logger.LogWarning("Webhook rejeitado: assinatura ausente ou inválida.");
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        IReadOnlyList<InboundMessage> messages;
        try
        {
            messages = MetaPayloadParser.Parse(body);
        }
        catch (JsonException)
        {
            // Assinatura válida mas corpo ilegível: reentregar não conserta. 200 evita um laço de retry.
            logger.LogError("Webhook com assinatura válida e payload ilegível.");
            return Results.Ok();
        }

        var needsRetry = false;

        // Sequencial: duas mensagens do mesmo cliente precisam entrar na conversa em ordem.
        foreach (var message in messages)
        {
            var result = await conversation.HandleAsync(message, cancellationToken);

            if (result.IsFailure)
            {
                // Sem telefone nem texto no log — só códigos e o id técnico da Meta.
                logger.LogError(
                    "Falha ao tratar a mensagem {MessageId}: {ErrorCode} (transitório: {Transient}).",
                    message.WhatsAppMessageId, result.Error.Code, result.Error.IsTransient);

                needsRetry |= result.Error.IsTransient;
            }
        }

        return needsRetry ? Results.StatusCode(StatusCodes.Status500InternalServerError) : Results.Ok();
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
