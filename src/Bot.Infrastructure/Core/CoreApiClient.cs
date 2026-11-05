using System.Net;
using System.Text.Json;
using Advocacia.Bot.Application;
using Advocacia.Bot.Application.Abstractions;
using Refit;

namespace Advocacia.Bot.Infrastructure.Core;

/// <summary>
/// Implementa <see cref="ICoreApiClient"/> sobre o contrato Refit. Traduz respostas HTTP em
/// <see cref="Result"/>: falha de rede, 5xx e 429 são TRANSITÓRIAS (vale reentregar); 4xx é
/// definitivo. O <c>errorCode</c> do ProblemDetails do Core é preservado — o fluxo precisa
/// reconhecer <c>ATENDIMENTO_BOT_BLOCKED</c>.
///
/// Nunca loga corpo de mensagem nem telefone.
/// </summary>
public sealed class CoreApiClient(ICoreApi api) : ICoreApiClient
{
    public Task<Result<AtendimentoRef>> StartAtendimentoAsync(
        WhatsAppChannel channel, string phoneNumber, string? contactName, CancellationToken cancellationToken) =>
        CallAsync(
            () => api.StartAsync(channel.CoreApiKey, new StartAtendimentoBody(phoneNumber, contactName), cancellationToken),
            reply => new AtendimentoRef(reply.AtendimentoId, reply.Status, reply.Created));

    public Task<Result<string>> GetStatusAsync(WhatsAppChannel channel, Guid atendimentoId, CancellationToken cancellationToken) =>
        CallAsync(() => api.GetStatusAsync(channel.CoreApiKey, atendimentoId, cancellationToken), reply => reply.Status);

    public Task<Result<Guid>> SendMessageAsync(
        WhatsAppChannel channel,
        Guid atendimentoId,
        MessageDirection direction,
        MessageSender sender,
        string body,
        string? externalId,
        CancellationToken cancellationToken) =>
        CallAsync(
            () => api.AddMessageAsync(
                channel.CoreApiKey, atendimentoId,
                new MessageBody(direction.ToString(), sender.ToString(), body, externalId), cancellationToken),
            reply => reply.MessageId);

    public async Task<Result> RequestHandoffAsync(WhatsAppChannel channel, Guid atendimentoId, CancellationToken cancellationToken)
    {
        var result = await CallAsync(
            () => api.HandoffAsync(channel.CoreApiKey, atendimentoId, cancellationToken),
            reply => reply.Success);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    private static async Task<Result<TOut>> CallAsync<TIn, TOut>(Func<Task<IApiResponse<TIn>>> call, Func<TIn, TOut> map)
    {
        IApiResponse<TIn>? response;

        try
        {
            response = await call();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ApiException)
        {
            return Result.Failure<TOut>(Error.Transient("CORE_UNREACHABLE", "Não foi possível falar com o Core."));
        }

        using (response)
        {
            if (response!.IsSuccessStatusCode && response.Content is not null)
            {
                return Result.Success(map(response.Content));
            }

            return Result.Failure<TOut>(ToError(response.StatusCode, response.Error?.Content));
        }
    }

    private static Error ToError(HttpStatusCode status, string? problemJson)
    {
        var code = ReadErrorCode(problemJson) ?? $"CORE_HTTP_{(int)status}";
        // 401/403 contam como transitórios DE PROPÓSITO: significam que a chave do escritório foi
        // revogada ou expirou — incidente de operação, não erro do cliente. Tratá-los como
        // definitivos faria o Bot descartar a mensagem; como transitórios, a Meta reentrega (por
        // até 7 dias) enquanto alguém troca a chave, e nada se perde. Sai com código próprio para alertar.
        var transient = status is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError
            or HttpStatusCode.RequestTimeout or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        return transient
            ? Error.Transient(code, $"Core respondeu {(int)status}.")
            : Error.Permanent(code, $"Core respondeu {(int)status}.");
    }

    private static string? ReadErrorCode(string? problemJson)
    {
        if (string.IsNullOrWhiteSpace(problemJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(problemJson);
            return document.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
