using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Advocacia.Bot.Application;
using Advocacia.Bot.Application.Abstractions;
using Advocacia.Bot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Advocacia.Bot.Infrastructure.Meta;

/// <summary>
/// Envio pela WhatsApp Cloud API OFICIAL (<c>graph.facebook.com</c>). Nenhuma API não oficial.
///
/// Atenção operacional: a Meta só aceita texto livre dentro da janela de 24h desde a última
/// mensagem do cliente; fora dela, a resposta é um erro 4xx (131047) que chega ao advogado como
/// mensagem "Failed". Enviar fora da janela exige template aprovado — fora do escopo desta etapa.
///
/// Telefone e texto são dado pessoal e sigiloso: nunca vão para o log.
/// </summary>
public sealed class MetaWhatsAppClient(
    HttpClient httpClient, IOptions<WhatsAppOptions> options, ILogger<MetaWhatsAppClient> logger) : IWhatsAppClient
{
    public async Task<Result<string>> SendTextAsync(
        WhatsAppChannel channel, string toPhoneNumber, string body, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var url = $"{settings.GraphApiBaseUrl.TrimEnd('/')}/{settings.GraphApiVersion}/{channel.PhoneNumberId}/messages";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new SendTextPayload(
                "whatsapp", "individual", toPhoneNumber, "text", new TextPayload(false, body))),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channel.AccessToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Falha de rede ao enviar mensagem pela Meta (número {PhoneNumberId}).", channel.PhoneNumberId);
            return Result.Failure<string>(Error.Transient("META_UNREACHABLE", "Não foi possível falar com a Meta."));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                logger.LogWarning(
                    "Meta respondeu {StatusCode} ao enviar mensagem (número {PhoneNumberId}).",
                    (int)response.StatusCode, channel.PhoneNumberId);

                var code = $"META_HTTP_{(int)response.StatusCode}";
                return transient
                    ? Result.Failure<string>(Error.Transient(code, "A Meta recusou temporariamente o envio."))
                    : Result.Failure<string>(Error.Permanent(code, "A Meta recusou o envio."));
            }

            var reply = await response.Content.ReadFromJsonAsync<SendTextReply>(cancellationToken);
            var wamid = reply?.Messages?.FirstOrDefault()?.Id;

            return string.IsNullOrWhiteSpace(wamid)
                ? Result.Failure<string>(Error.Permanent("META_INVALID_RESPONSE", "A Meta não devolveu o id da mensagem."))
                : Result.Success(wamid);
        }
    }

    private sealed record SendTextPayload(
        [property: JsonPropertyName("messaging_product")] string MessagingProduct,
        [property: JsonPropertyName("recipient_type")] string RecipientType,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] TextPayload Text);

    private sealed record TextPayload(
        [property: JsonPropertyName("preview_url")] bool PreviewUrl,
        [property: JsonPropertyName("body")] string Body);

    private sealed record SendTextReply([property: JsonPropertyName("messages")] List<SentMessage>? Messages);

    private sealed record SentMessage([property: JsonPropertyName("id")] string? Id);
}
