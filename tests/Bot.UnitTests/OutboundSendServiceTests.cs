using Advocacia.Bot.Application;
using Advocacia.Bot.Application.Outbound;
using FluentAssertions;

namespace Advocacia.Bot.UnitTests;

public class OutboundSendServiceTests
{
    private readonly FakeWhatsApp _whatsApp = new();
    private readonly InMemoryLedger _ledger = new();

    private OutboundSendService Service() => new(new FakeChannelRegistry(Samples.Channel()), _whatsApp, _ledger);

    private static SendRequest Request(Guid? messageId = null, Guid? tenant = null) =>
        new(tenant ?? Samples.TenantId, Guid.NewGuid(), messageId ?? Guid.NewGuid(), "5585999998888", "Bom dia, Maria.");

    [Fact]
    public async Task SendsTheLawyersMessageThroughWhatsApp_AndReturnsTheWamid()
    {
        var result = await Service().SendAsync(Request(), default);

        result.Value.Should().Be("wamid.OUT1");
        _whatsApp.Sent.Should().ContainSingle().Which.Should().Be(("5585999998888", "Bom dia, Maria."));
    }

    [Fact]
    public async Task ARepeatedRequest_DoesNotSendTwice_AndReturnsTheSameWamid()
    {
        var request = Request();
        var service = Service();

        var first = await service.SendAsync(request, default);
        var second = await service.SendAsync(request, default);

        second.Value.Should().Be(first.Value);
        _whatsApp.Sent.Should().HaveCount(1, "o cliente não pode receber a mensagem do advogado duas vezes");
    }

    [Fact]
    public async Task ARequestWhileTheSameMessageIsBeingSent_IsRefusedAsTransient()
    {
        var request = Request();
        await _ledger.ClaimAsync($"out:{request.MessageId}", default);

        var result = await Service().SendAsync(request, default);

        result.Error.Code.Should().Be(OutboundErrorCodes.InProgress);
        result.Error.IsTransient.Should().BeTrue();
        _whatsApp.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenWhatsAppFails_TheClaimIsReleased_SoTheRetryCanSend()
    {
        var request = Request();
        var service = Service();
        _whatsApp.FailNextWith = Error.Transient("META_HTTP_503", "x");

        var first = await service.SendAsync(request, default);
        var retry = await service.SendAsync(request, default);

        first.IsFailure.Should().BeTrue();
        retry.IsSuccess.Should().BeTrue();
        _whatsApp.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task ATenantWithoutAConfiguredNumber_IsRefused_WithoutSending()
    {
        var result = await Service().SendAsync(Request(tenant: Guid.NewGuid()), default);

        result.Error.Code.Should().Be(OutboundErrorCodes.UnknownTenant);
        _whatsApp.Sent.Should().BeEmpty();
    }
}
