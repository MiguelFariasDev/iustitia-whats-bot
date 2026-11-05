using System.Net;
using Advocacia.Bot.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Advocacia.Bot.IntegrationTests;

/// <summary>O webhook da Meta de ponta a ponta: assinatura, idempotência, handoff e silêncio com humano.</summary>
public class WebhookTests : IClassFixture<BotApiFactory>
{
    private const string Route = "/webhooks/whatsapp";
    private readonly BotApiFactory _factory;
    private readonly HttpClient _client;

    public WebhookTests(BotApiFactory factory)
    {
        _factory = factory;
        _factory.Core.Reset();
        _factory.Meta.Reset();
        _client = factory.CreateClient();
    }

    private static string NewId() => "wamid." + Guid.NewGuid().ToString("N");

    private static string Payload(string text, string? id = null, string phoneNumberId = BotApiFactory.PhoneNumberId, string from = "5585999998888") => $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"558533334444","phone_number_id":"{{{phoneNumberId}}}"},
          "contacts":[{"profile":{"name":"Maria Souza"},"wa_id":"{{{from}}}"}],
          "messages":[{"from":"{{{from}}}","id":"{{{id ?? NewId()}}}","timestamp":"1730000000","text":{"body":"{{{text}}}"},"type":"text"}]
        }}]}]}
        """;

    private async Task<HttpResponseMessage> PostAsync(string json, string? signatureOverride = null, bool sign = true)
    {
        var (body, signature) = BotApiFactory.Sign(json);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        if (sign)
        {
            request.Headers.Add("X-Hub-Signature-256", signatureOverride ?? signature);
        }

        return await _client.SendAsync(request);
    }

    // ---- verificação do webhook (GET) ----

    [Fact]
    public async Task Verification_EchoesTheChallenge_WhenTheTokenMatches()
    {
        var response = await _client.GetAsync(
            $"{Route}?hub.mode=subscribe&hub.verify_token={BotApiFactory.VerifyToken}&hub.challenge=1158201444");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("1158201444");
    }

    [Theory]
    [InlineData("subscribe", "token-errado", "123")]
    [InlineData("unsubscribe", BotApiFactory.VerifyToken, "123")]
    [InlineData("subscribe", "", "123")]
    [InlineData("subscribe", BotApiFactory.VerifyToken, "")]
    public async Task Verification_IsRefused_ForAWrongTokenModeOrMissingChallenge(string mode, string token, string challenge)
    {
        var response = await _client.GetAsync($"{Route}?hub.mode={mode}&hub.verify_token={token}&hub.challenge={challenge}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Verification_IsRefused_WithNoParameters()
    {
        (await _client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- assinatura: a única prova de que o POST veio da Meta ----

    [Fact]
    public async Task APostWithoutASignature_IsRejected_AndNothingIsProcessed()
    {
        var response = await PostAsync(Payload("oi"), sign: false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Core.Calls.Should().BeEmpty();
        _factory.Meta.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task APostWithAWrongSignature_IsRejected_AndNothingIsProcessed()
    {
        var response = await PostAsync(Payload("oi"), signatureOverride: "sha256=" + new string('a', 64));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Core.Calls.Should().BeEmpty();
        _factory.Meta.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task APostSignedWithTheWrongSecret_IsRejected()
    {
        var (body, signature) = BotApiFactory.Sign(Payload("oi"), secret: "segredo-de-um-atacante");
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Hub-Signature-256", signature);

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Core.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ABodyAlteredAfterSigning_IsRejected()
    {
        var original = Payload("oi", "wamid.original");
        var (_, signature) = BotApiFactory.Sign(original);
        var tamperedJson = original.Replace("\"body\":\"oi\"", "\"body\":\"ola\"");
        tamperedJson.Should().NotBe(original, "o teste só vale se a adulteração de fato mudar o corpo");
        var tampered = System.Text.Encoding.UTF8.GetBytes(tamperedJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = new ByteArrayContent(tampered) };
        request.Headers.Add("X-Hub-Signature-256", signature);

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Core.Calls.Should().BeEmpty();
    }

    // ---- o fluxo ----

    [Fact]
    public async Task AValidMessage_FlowsThroughTheCore_AndTheClientGetsTheMenu()
    {
        var response = await PostAsync(Payload("oi"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Core.Calls.Should().Equal("start", "message:Inbound:Client", "status", "message:Outbound:Bot");
        _factory.Meta.Sent.Should().ContainSingle().Which.Should().Match<(string PhoneNumberId, string To, string Body, string? Authorization)>(m =>
            m.PhoneNumberId == "PNID-1" && m.To == "5585999998888" && m.Body.Contains("*2* - Falar com um advogado")
            && m.Authorization == "Bearer token-meta");
        _factory.Core.Messages[0].Body.Should().Be("oi");
    }

    [Fact]
    public async Task TheClientTypingTwo_StartsTheHandoff_AndGetsTheWaitNotice_ThenTheBotGoesSilent()
    {
        (await PostAsync(Payload("2"))).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Core.Status.Should().Be("PendingHuman");
        _factory.Core.Calls.Should().Equal("start", "message:Inbound:Client", "status", "handoff", "message:Outbound:Bot");
        _factory.Meta.Sent.Should().ContainSingle().Which.Body.Should().Be("Aguarde, um advogado vai te atender.");

        _factory.Meta.Sent.Clear();
        (await PostAsync(Payload("tem alguém aí?"))).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Meta.Sent.Should().BeEmpty("depois do handoff o bot não responde mais");
    }

    [Theory]
    [InlineData("Human")]
    [InlineData("PendingHuman")]
    public async Task WithAHumanOnTheConversation_TheBotNeverSendsAnything_ButTheMessageIsRecorded(string status)
    {
        _factory.Core.Status = status;

        (await PostAsync(Payload("oi, doutora"))).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Meta.Sent.Should().BeEmpty();
        _factory.Core.Messages.Should().ContainSingle().Which.Body.Should().Be("oi, doutora");
        _factory.Core.Calls.Should().NotContain("message:Outbound:Bot");
    }

    [Fact]
    public async Task WhenTheLawyerHandsTheConversationBack_TheBotAnswersAgain()
    {
        _factory.Core.Status = "Human";
        await PostAsync(Payload("oi"));
        _factory.Meta.Sent.Should().BeEmpty();

        _factory.Core.Status = "Bot";
        await PostAsync(Payload("oi de novo"));

        _factory.Meta.Sent.Should().ContainSingle();
    }

    // ---- idempotência real, no PostgreSQL ----

    [Fact]
    public async Task ARedeliveredWebhook_IsAcknowledged_ButAnsweredOnlyOnce()
    {
        var json = Payload("oi", "wamid.REENTREGA");

        var first = await PostAsync(json);
        var second = await PostAsync(json);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "a Meta precisa do 200 para parar de reentregar");
        _factory.Meta.Sent.Should().HaveCount(1, "o cliente não pode receber a resposta duas vezes");
        _factory.Core.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task TheSameWebhookDeliveredConcurrently_IsProcessedExactlyOnce()
    {
        var json = Payload("oi", "wamid.CONCORRENTE");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostAsync(json)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        _factory.Meta.Sent.Should().HaveCount(1, "o INSERT ... ON CONFLICT do livro é atômico");
    }

    [Fact]
    public async Task WhenTheCoreIsDown_TheBotAsksTheMetaToRedeliver_AndTheRetryWorksWithoutDuplicating()
    {
        var json = Payload("oi", "wamid.CORE_FORA");
        _factory.Core.Down = true;

        var failed = await PostAsync(json);

        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        _factory.Meta.Sent.Should().BeEmpty();

        _factory.Core.Down = false;
        var retry = await PostAsync(json);

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Meta.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenTheMetaRefusesTemporarily_TheBotAsksForRedelivery_AndTheRetrySendsOnce()
    {
        var json = Payload("oi", "wamid.META_FORA");
        _factory.Meta.FailWith = HttpStatusCode.ServiceUnavailable;

        var failed = await PostAsync(json);
        var retry = await PostAsync(json);

        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Meta.Sent.Should().HaveCount(1);
        _factory.Core.Messages.Where(m => m.Direction == "Outbound").Should().HaveCount(1, "o Core deduplica pelo externalId determinístico");
    }

    [Fact]
    public async Task ARevokedCoreKey_AsksForRedelivery_SoNoMessageIsLostWhileOpsFixesIt()
    {
        var factoryWithBadKey = new BadKeyFactory();
        await factoryWithBadKey.InitializeAsync();
        try
        {
            var client = factoryWithBadKey.CreateClient();
            var (body, signature) = BotApiFactory.Sign(Payload("oi"));
            using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = new ByteArrayContent(body) };
            request.Headers.Add("X-Hub-Signature-256", signature);

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            factoryWithBadKey.Meta.Sent.Should().BeEmpty();
        }
        finally
        {
            await ((IAsyncLifetime)factoryWithBadKey).DisposeAsync();
            factoryWithBadKey.Dispose();
        }
    }

    // ---- outros payloads ----

    [Fact]
    public async Task DeliveryStatusNotifications_AreAcknowledged_AndIgnored()
    {
        const string statuses = """
            {"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"PNID-1"},
              "statuses":[{"id":"wamid.X","status":"delivered","recipient_id":"5585999998888"}]}}]}]}
            """;

        (await PostAsync(statuses)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Core.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AMessageToAnUnconfiguredNumber_IsAcknowledged_WithoutTouchingTheCore()
    {
        (await PostAsync(Payload("oi", phoneNumberId: "NUMERO-DESCONHECIDO"))).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Core.Calls.Should().BeEmpty();
        _factory.Meta.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AValidlySignedButUnreadableBody_IsAcknowledged_SoTheMetaDoesNotRetryForever()
    {
        (await PostAsync("{ isto nao e json")).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Core.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOversizedBody_IsRefused()
    {
        var huge = new string('a', 1_100_000);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = new StringContent(huge) };

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ABatchWithTwoMessages_IsAnsweredInOrder()
    {
        const string batch = """
            {"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"PNID-1"},
              "messages":[
                {"from":"5585911112222","id":"wamid.LOTE1","type":"text","text":{"body":"primeira"}},
                {"from":"5585911112222","id":"wamid.LOTE2","type":"text","text":{"body":"segunda"}}]}}]}]}
            """;

        (await PostAsync(batch)).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Core.Messages.Where(m => m.Direction == "Inbound").Select(m => m.Body).Should().Equal("primeira", "segunda");
    }

    [Fact]
    public async Task TheLedgerKeepsOnlyIdsAndResults_NeverThePhoneNorTheText()
    {
        await PostAsync(Payload("meu CPF é 111.444.777-35", "wamid.PRIVACIDADE", from: "5585977776666"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var rows = await db.IdempotencyRecords.AsNoTracking().Where(r => r.Key == "in:wamid.PRIVACIDADE").ToListAsync();

        var dump = string.Join('|', rows.Select(r => $"{r.Key}={r.Result}"));
        rows.Should().ContainSingle().Which.State.Should().Be(IdempotencyState.Done);
        dump.Should().NotContain("5585977776666").And.NotContain("111.444.777-35");
    }

    private sealed class BadKeyFactory : BotApiFactory
    {
        public BadKeyFactory()
        {
            Core.Down = false;
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("WhatsApp:Channels:0:CoreApiKey", "adv_bot_chave_revogada");
        }
    }
}
