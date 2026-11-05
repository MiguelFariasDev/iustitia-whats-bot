using System.Net;
using System.Text;
using System.Text.Json;
using Advocacia.Bot.Application.Abstractions;
using Advocacia.Bot.Infrastructure.Configuration;
using Advocacia.Bot.Infrastructure.Core;
using Advocacia.Bot.Infrastructure.Meta;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Refit;

namespace Advocacia.Bot.UnitTests;

/// <summary>Um HttpMessageHandler que devolve respostas prontas e guarda o que foi enviado.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, headers, body));

        var response = respond(request);

        // O HttpClient real sempre preenche RequestMessage; o Refit depende disso ao montar erros.
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// O contrato HTTP com o Core, exercitado de verdade (Refit real, handler falso): rotas, cabeçalho
/// de autenticação, corpo em camelCase e a tradução de erros em Result.
/// </summary>
public class CoreApiClientTests
{
    private static readonly Guid AtendimentoId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (CoreApiClient Client, StubHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var api = RestService.For<ICoreApi>(new HttpClient(handler) { BaseAddress = new Uri("https://core.test") });
        return (new CoreApiClient(api), handler);
    }

    [Fact]
    public async Task Start_PostsThePhoneAndName_WithTheChannelsApiKey_AndParsesTheReply()
    {
        var (client, handler) = Build(_ => StubHandler.Json(
            HttpStatusCode.OK, $$"""{"atendimentoId":"{{AtendimentoId}}","status":"Bot","created":true}"""));

        var result = await client.StartAtendimentoAsync(Samples.Channel(), "5585999998888", "Maria", default);

        result.Value.Should().Be(new AtendimentoRef(AtendimentoId, "Bot", true));
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/api/v1/bot/atendimentos");
        request.Headers["X-Api-Key"].Should().Be("adv_bot_core_key");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("phoneNumber").GetString().Should().Be("5585999998888");
        body.RootElement.GetProperty("contactName").GetString().Should().Be("Maria");
    }

    [Fact]
    public async Task SendMessage_PostsToTheConversation_WithDirectionSenderBodyAndExternalId()
    {
        var messageId = Guid.NewGuid();
        var (client, handler) = Build(_ => StubHandler.Json(HttpStatusCode.OK, $$"""{"messageId":"{{messageId}}"}"""));

        var result = await client.SendMessageAsync(
            Samples.Channel(), AtendimentoId, MessageDirection.Outbound, MessageSender.Bot, "Menu", "bot:wamid.1", default);

        result.Value.Should().Be(messageId);
        var request = handler.Requests.Single();
        request.Path.Should().Be($"/api/v1/bot/atendimentos/{AtendimentoId}/mensagens");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("direction").GetString().Should().Be("Outbound");
        body.RootElement.GetProperty("sender").GetString().Should().Be("Bot");
        body.RootElement.GetProperty("body").GetString().Should().Be("Menu");
        body.RootElement.GetProperty("externalId").GetString().Should().Be("bot:wamid.1");
    }

    [Fact]
    public async Task GetStatus_CallsTheStatusRoute()
    {
        var (client, handler) = Build(_ => StubHandler.Json(
            HttpStatusCode.OK, $$"""{"atendimentoId":"{{AtendimentoId}}","status":"Human"}"""));

        var result = await client.GetStatusAsync(Samples.Channel(), AtendimentoId, default);

        result.Value.Should().Be("Human");
        handler.Requests.Single().Should().Match<(HttpMethod, string, IReadOnlyDictionary<string, string>, string)>(r =>
            r.Item1 == HttpMethod.Get && r.Item2 == $"/api/v1/bot/atendimentos/{AtendimentoId}/status");
    }

    [Fact]
    public async Task Handoff_PostsToTheHandoffRoute()
    {
        var (client, handler) = Build(_ => StubHandler.Json(HttpStatusCode.OK, """{"success":true}"""));

        var result = await client.RequestHandoffAsync(Samples.Channel(), AtendimentoId, default);

        result.IsSuccess.Should().BeTrue();
        handler.Requests.Single().Path.Should().Be($"/api/v1/bot/atendimentos/{AtendimentoId}/handoff");
    }

    [Fact]
    public async Task TheBotBlockedErrorCode_SurvivesTheTranslation_BecauseTheFlowDependsOnIt()
    {
        var (client, _) = Build(_ => StubHandler.Json(
            HttpStatusCode.Conflict, """{"title":"x","status":409,"errorCode":"ATENDIMENTO_BOT_BLOCKED"}"""));

        var result = await client.SendMessageAsync(
            Samples.Channel(), AtendimentoId, MessageDirection.Outbound, MessageSender.Bot, "Menu", "bot:1", default);

        result.Error.Code.Should().Be(CoreErrorCodes.BotBlocked);
        result.Error.IsTransient.Should().BeFalse("repetir não faz o Core aceitar a fala do bot");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ServerSideAndThrottlingErrors_AreTransient(HttpStatusCode status)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var result = await client.GetStatusAsync(Samples.Channel(), AtendimentoId, default);

        result.Error.IsTransient.Should().BeTrue();
        result.Error.Code.Should().Be($"CORE_HTTP_{(int)status}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARevokedOrExpiredKey_IsTransient_SoTheMetaKeepsRedeliveringWhileOpsFixesIt(HttpStatusCode status)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var result = await client.StartAtendimentoAsync(Samples.Channel(), "5585999998888", null, default);

        result.Error.IsTransient.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task OtherClientErrors_ArePermanent(HttpStatusCode status)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var result = await client.GetStatusAsync(Samples.Channel(), AtendimentoId, default);

        result.Error.IsTransient.Should().BeFalse();
    }

    [Fact]
    public async Task ANetworkFailure_IsTransient()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("connection refused"));

        var result = await client.GetStatusAsync(Samples.Channel(), AtendimentoId, default);

        result.Error.Should().Match<Advocacia.Bot.Application.Error>(e => e.IsTransient && e.Code == "CORE_UNREACHABLE");
    }

    [Fact]
    public async Task EachChannelAuthenticatesWithItsOwnKey()
    {
        var (client, handler) = Build(_ => StubHandler.Json(
            HttpStatusCode.OK, $$"""{"atendimentoId":"{{AtendimentoId}}","status":"Bot"}"""));
        var other = Samples.Channel();
        other.CoreApiKey = "adv_bot_outra_chave";

        await client.GetStatusAsync(Samples.Channel(), AtendimentoId, default);
        await client.GetStatusAsync(other, AtendimentoId, default);

        handler.Requests.Select(r => r.Headers["X-Api-Key"]).Should().Equal("adv_bot_core_key", "adv_bot_outra_chave");
    }
}

public class MetaWhatsAppClientTests
{
    private static (MetaWhatsAppClient Client, StubHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var options = Options.Create(new WhatsAppOptions { GraphApiVersion = "v21.0", GraphApiBaseUrl = "https://graph.test" });
        return (new MetaWhatsAppClient(new HttpClient(handler), options, NullLogger<MetaWhatsAppClient>.Instance), handler);
    }

    [Fact]
    public async Task SendsATextMessageToTheOfficialCloudApi_AndReturnsTheWamid()
    {
        var (client, handler) = Build(_ => StubHandler.Json(
            HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.SENT1"}]}"""));

        var result = await client.SendTextAsync(Samples.Channel(), "5585999998888", "Olá!", default);

        result.Value.Should().Be("wamid.SENT1");
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/v21.0/PNID-1/messages");
        request.Headers["Authorization"].Should().Be("Bearer meta-token");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("messaging_product").GetString().Should().Be("whatsapp");
        body.RootElement.GetProperty("to").GetString().Should().Be("5585999998888");
        body.RootElement.GetProperty("type").GetString().Should().Be("text");
        body.RootElement.GetProperty("text").GetProperty("body").GetString().Should().Be("Olá!");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task MetaErrors_AreClassifiedAsTransientOrPermanent(HttpStatusCode status, bool transient)
    {
        var (client, _) = Build(_ => StubHandler.Json(status, """{"error":{"message":"x","code":131047}}"""));

        var result = await client.SendTextAsync(Samples.Channel(), "5585999998888", "Olá", default);

        result.Error.IsTransient.Should().Be(transient);
        result.Error.Code.Should().Be($"META_HTTP_{(int)status}");
    }

    [Fact]
    public async Task ASuccessWithoutAMessageId_IsAnError_NotASilentSuccess()
    {
        var (client, _) = Build(_ => StubHandler.Json(HttpStatusCode.OK, """{"messages":[]}"""));

        var result = await client.SendTextAsync(Samples.Channel(), "5585999998888", "Olá", default);

        result.Error.Code.Should().Be("META_INVALID_RESPONSE");
    }

    [Fact]
    public async Task ANetworkFailure_IsTransient()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("dns"));

        var result = await client.SendTextAsync(Samples.Channel(), "5585999998888", "Olá", default);

        result.Error.IsTransient.Should().BeTrue();
    }
}
