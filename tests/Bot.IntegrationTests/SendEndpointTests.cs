using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Advocacia.Bot.IntegrationTests;

/// <summary>O Core pedindo ao Bot a entrega da mensagem do advogado: <c>POST /api/v1/bot/send</c>.</summary>
public class SendEndpointTests : IClassFixture<BotApiFactory>
{
    private const string Route = "/api/v1/bot/send";
    private readonly BotApiFactory _factory;

    public SendEndpointTests(BotApiFactory factory)
    {
        _factory = factory;
        _factory.Core.Reset();
        _factory.Meta.Reset();
    }

    private HttpClient Client(string? key = BotApiFactory.InboundKey)
    {
        var client = _factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    private static object Body(Guid? messageId = null, Guid? tenant = null, string to = "5585999998888", string body = "Bom dia, Maria.") =>
        new { tenantId = tenant ?? BotApiFactory.TenantId, atendimentoId = Guid.NewGuid(), messageId = messageId ?? Guid.NewGuid(), to, body };

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task DeliversTheLawyersMessageThroughTheMeta_AndReturnsTheWamid()
    {
        var response = await Client().PostAsJsonAsync(Route, Body());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("externalId").GetString().Should().StartWith("wamid.OUT");
        _factory.Meta.Sent.Should().ContainSingle().Which.Should().Match<(string PhoneNumberId, string To, string Body, string? Authorization)>(m =>
            m.PhoneNumberId == "PNID-1" && m.To == "5585999998888" && m.Body == "Bom dia, Maria.");
    }

    [Fact]
    public async Task TheCoreRepeatingTheRequest_DoesNotMakeTheClientReceiveTheMessageTwice()
    {
        var messageId = Guid.NewGuid();

        var first = await Client().PostAsJsonAsync(Route, Body(messageId));
        var second = await Client().PostAsJsonAsync(Route, Body(messageId));

        var firstId = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("externalId").GetString();
        var secondId = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("externalId").GetString();
        secondId.Should().Be(firstId);
        _factory.Meta.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameMessage_SendItOnce()
    {
        var messageId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Client().PostAsJsonAsync(Route, Body(messageId))));

        _factory.Meta.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenTheMetaFails_TheCoreGetsAnError_AndALaterRetrySucceeds()
    {
        var messageId = Guid.NewGuid();
        _factory.Meta.FailWith = HttpStatusCode.ServiceUnavailable;

        var failed = await Client().PostAsJsonAsync(Route, Body(messageId));
        var retry = await Client().PostAsJsonAsync(Route, Body(messageId));

        failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Meta.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task ATenantWithoutAConfiguredNumber_IsRefused()
    {
        var response = await Client().PostAsJsonAsync(Route, Body(tenant: Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("BOT_UNKNOWN_TENANT");
        _factory.Meta.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("chave-errada")]
    [InlineData("")]
    public async Task WithoutTheRightKey_NothingIsSent(string? key)
    {
        var response = await Client(key).PostAsJsonAsync(Route, Body());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Meta.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task TheCoresOwnKey_IsNotAcceptedHere_BecauseTheTwoCredentialsAreIndependent()
    {
        var response = await Client(FakeCoreServer.ValidKey).PostAsJsonAsync(Route, Body());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("123", "texto")]
    [InlineData("5585999998888", "")]
    [InlineData("5585999998888", "   ")]
    public async Task MalformedRequests_AreRejected(string to, string body)
    {
        var response = await Client().PostAsJsonAsync(Route, Body(to: to, body: body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Meta.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ATextOverTheWhatsAppLimit_IsRejected()
    {
        var response = await Client().PostAsJsonAsync(Route, Body(body: new string('a', 4097)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task APhoneWithFormatting_IsNormalizedToDigits()
    {
        await Client().PostAsJsonAsync(Route, Body(to: "+55 (85) 99999-8888"));

        _factory.Meta.Sent.Single().To.Should().Be("5585999998888");
    }

    [Fact]
    public async Task TheHealthEndpoints_Respond()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>O Bot recusa subir sem a configuração de segurança — melhor falhar na subida que em produção.</summary>
public class StartupValidationTests
{
    [Fact]
    public async Task WithoutTheAppSecret_TheBotRefusesToStart_NamingTheMissingKeyButNoValues()
    {
        var factory = new BotApiFactory { AppSecretOverride = null };
        await factory.InitializeAsync();

        try
        {
            var act = () => factory.CreateClient();

            var exception = act.Should().Throw<InvalidOperationException>().Which;
            exception.Message.Should().Contain("WhatsApp:AppSecret");
            exception.Message.Should().NotContain(BotApiFactory.InboundKey).And.NotContain(FakeCoreServer.ValidKey);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
            factory.Dispose();
        }
    }
}
