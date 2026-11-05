using Advocacia.Bot.Application;
using Advocacia.Bot.Application.Abstractions;
using Advocacia.Bot.Application.Conversations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Advocacia.Bot.UnitTests;

/// <summary>A máquina de estados do Bot: o que ele faz — e, principalmente, o que NÃO faz — em cada status.</summary>
public class ConversationServiceTests
{
    private readonly FakeCore _core = new();
    private readonly FakeWhatsApp _whatsApp = new();
    private readonly InMemoryLedger _ledger = new();

    private ConversationService Service(params WhatsAppChannel[] channels) => new(
        new FakeChannelRegistry(channels.Length == 0 ? [Samples.Channel()] : channels),
        _core, _whatsApp, _ledger, NullLogger<ConversationService>.Instance);

    // ---- status Bot: o fluxo automático ----

    [Fact]
    public async Task WhenTheBotIsInCharge_ItRecordsTheClientMessage_RepliesWithTheMenu_AndSendsItToWhatsApp()
    {
        var result = await Service().HandleAsync(Samples.Text("oi"), default);

        result.Value.Should().Be(InboundOutcome.Replied);
        _core.Messages.Should().HaveCount(2);
        _core.Messages[0].Should().Match<(MessageDirection, MessageSender, string, string?)>(m =>
            m.Item1 == MessageDirection.Inbound && m.Item2 == MessageSender.Client && m.Item3 == "oi" && m.Item4 == "wamid.IN1");
        _core.Messages[1].Should().Match<(MessageDirection, MessageSender, string, string?)>(m =>
            m.Item1 == MessageDirection.Outbound && m.Item2 == MessageSender.Bot && m.Item4 == "bot:wamid.IN1");
        _whatsApp.Sent.Should().ContainSingle().Which.Should().Match<(string To, string Body)>(m =>
            m.To == "5585999998888" && m.Body.Contains("*2* - Falar com um advogado"));
    }

    [Fact]
    public async Task TheClientMessage_IsRecordedBeforeAnythingIsSent_AndTheStatusIsCheckedBeforeReplying()
    {
        await Service().HandleAsync(Samples.Text("oi"), default);

        _core.Calls.Should().Equal("start", "message:Inbound:Client", "status", "message:Outbound:Bot");
    }

    [Fact]
    public async Task TheBotRecordsItsReplyInTheCoreBeforeSendingToWhatsApp_SoTheCoreCanVetoIt()
    {
        var order = new List<string>();
        var core = new OrderTrackingCore(_core, order);
        var whatsApp = new OrderTrackingWhatsApp(_whatsApp, order);
        var service = new ConversationService(
            new FakeChannelRegistry(Samples.Channel()), core, whatsApp, _ledger, NullLogger<ConversationService>.Instance);

        await service.HandleAsync(Samples.Text("oi"), default);

        order.Should().Equal("core:Inbound", "core:status", "core:Outbound", "whatsapp");
    }

    // ---- status Human / PendingHuman: silêncio ----

    [Theory]
    [InlineData(AtendimentoStatuses.Human)]
    [InlineData(AtendimentoStatuses.PendingHuman)]
    public async Task WhenAHumanIsInvolved_TheBotNeverReplies(string status)
    {
        _core.Status = status;

        var result = await Service().HandleAsync(Samples.Text("oi"), default);

        result.Value.Should().Be(InboundOutcome.Silent);
        _whatsApp.Sent.Should().BeEmpty("com humano na conversa o bot não responde NADA");
        _core.Messages.Should().ContainSingle().Which.Item1.Should().Be(MessageDirection.Inbound);
    }

    [Theory]
    [InlineData(AtendimentoStatuses.Human)]
    [InlineData(AtendimentoStatuses.PendingHuman)]
    public async Task WhenAHumanIsInvolved_TheClientMessageIsStillRecordedForTheLawyerToRead(string status)
    {
        _core.Status = status;

        await Service().HandleAsync(Samples.Text("Doutora, é urgente"), default);

        _core.Messages.Should().ContainSingle().Which.Item3.Should().Be("Doutora, é urgente");
    }

    [Theory]
    [InlineData("2")]
    [InlineData("quero um humano")]
    public async Task WhenAHumanIsInvolved_EvenAskingForAHumanAgainDoesNotMakeTheBotTalk(string text)
    {
        _core.Status = AtendimentoStatuses.PendingHuman;

        await Service().HandleAsync(Samples.Text(text), default);

        _whatsApp.Sent.Should().BeEmpty();
        _core.Calls.Should().NotContain("handoff");
    }

    [Fact]
    public async Task TheStatusFromTheStartCallIsNotTrusted_TheFreshStatusDecides()
    {
        // "start" ainda diz Bot, mas quando o bot consulta de novo o advogado já assumiu.
        var core = new StaleStartCore(_core);
        var service = new ConversationService(
            new FakeChannelRegistry(Samples.Channel()), core, _whatsApp, _ledger, NullLogger<ConversationService>.Instance);

        var result = await service.HandleAsync(Samples.Text("oi"), default);

        result.Value.Should().Be(InboundOutcome.Silent);
        _whatsApp.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task IfTheLawyerTakesOverBetweenTheStatusCheckAndTheReply_TheCoreVetoes_AndNothingIsSent()
    {
        _core.LawyerTakesOverBeforeBotReplies = true;

        var result = await Service().HandleAsync(Samples.Text("oi"), default);

        result.IsSuccess.Should().BeTrue("ser vetado pelo Core não é erro: é o handoff funcionando");
        result.Value.Should().Be(InboundOutcome.Silent);
        _whatsApp.Sent.Should().BeEmpty();
    }

    // ---- handoff ----

    [Fact]
    public async Task WhenTheClientTypesTwo_TheBotRequestsTheHandoff_ThenTellsTheClientToWait()
    {
        var result = await Service().HandleAsync(Samples.Text("2"), default);

        result.Value.Should().Be(InboundOutcome.HandedOff);
        _core.Status.Should().Be(AtendimentoStatuses.PendingHuman);
        _core.Calls.Should().Equal("start", "message:Inbound:Client", "status", "handoff", "message:Outbound:Bot");
        _whatsApp.Sent.Should().ContainSingle().Which.Body.Should().Be("Aguarde, um advogado vai te atender.");
    }

    [Fact]
    public async Task AfterTheHandoff_TheBotStaysSilent_OnTheNextMessages()
    {
        var service = Service();
        await service.HandleAsync(Samples.Text("2", "wamid.A"), default);
        _whatsApp.Sent.Clear();

        var next = await service.HandleAsync(Samples.Text("alguém aí?", "wamid.B"), default);

        next.Value.Should().Be(InboundOutcome.Silent);
        _whatsApp.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task IfTheHandoffRequestFails_TheClientIsNotTold_ToWait_ForSomethingThatDidNotHappen()
    {
        _core.FailNextWith = null;
        var core = new FailingHandoffCore(_core);
        var service = new ConversationService(
            new FakeChannelRegistry(Samples.Channel()), core, _whatsApp, _ledger, NullLogger<ConversationService>.Instance);

        var result = await service.HandleAsync(Samples.Text("2"), default);

        result.IsFailure.Should().BeTrue();
        _whatsApp.Sent.Should().BeEmpty();
    }

    // ---- mídia ----

    [Fact]
    public async Task MediaIsRecordedAsAPlaceholder_AndTheClientIsToldItIsNotSupported()
    {
        var result = await Service().HandleAsync(Samples.Media(), default);

        result.Value.Should().Be(InboundOutcome.Replied);
        _core.Messages[0].Item3.Should().Contain("image");
        _whatsApp.Sent.Should().ContainSingle().Which.Body.Should().Contain("só consigo ler mensagens de texto");
    }

    [Fact]
    public async Task MediaFromAClientWithAHuman_IsRecorded_AndTheBotStaysSilent()
    {
        _core.Status = AtendimentoStatuses.Human;

        await Service().HandleAsync(Samples.Media(), default);

        _core.Messages.Should().ContainSingle();
        _whatsApp.Sent.Should().BeEmpty();
    }

    // ---- idempotência: a Meta reentrega ----

    [Fact]
    public async Task ARedeliveredWebhook_IsProcessedOnlyOnce()
    {
        var service = Service();
        await service.HandleAsync(Samples.Text("oi"), default);

        var second = await service.HandleAsync(Samples.Text("oi"), default);

        second.Value.Should().Be(InboundOutcome.Duplicate);
        _whatsApp.Sent.Should().HaveCount(1, "o cliente não pode receber a resposta duas vezes");
        _core.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task ADifferentMessageFromTheSameClient_IsNotADuplicate()
    {
        var service = Service();
        await service.HandleAsync(Samples.Text("oi", "wamid.A"), default);

        var second = await service.HandleAsync(Samples.Text("oi", "wamid.B"), default);

        second.Value.Should().Be(InboundOutcome.Replied);
    }

    [Fact]
    public async Task AMessageBeingProcessedRightNow_IsNotProcessedConcurrentlyByAnotherDelivery()
    {
        await _ledger.ClaimAsync("in:wamid.IN1", default);

        var result = await Service().HandleAsync(Samples.Text("oi"), default);

        result.Value.Should().Be(InboundOutcome.Duplicate);
        _core.Calls.Should().BeEmpty();
    }

    // ---- falhas: a Meta vai reentregar, então nada pode ficar "meio feito" e marcado como concluído ----

    [Fact]
    public async Task WhenTheCoreIsDown_TheFailureIsTransient_AndTheClaimIsReleasedSoTheRetryCanWork()
    {
        _core.FailNextWith = Error.Transient("CORE_UNREACHABLE", "fora do ar");
        var service = Service();

        var first = await service.HandleAsync(Samples.Text("oi"), default);

        first.IsFailure.Should().BeTrue();
        first.Error.IsTransient.Should().BeTrue();
        _ledger.Operations.Should().Contain("release:in:wamid.IN1").And.NotContain("complete:in:wamid.IN1");

        var retry = await service.HandleAsync(Samples.Text("oi"), default);
        retry.Value.Should().Be(InboundOutcome.Replied);
        _whatsApp.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenWhatsAppFailsToSend_TheClaimIsReleased_AndTheRetrySendsOnce()
    {
        _whatsApp.FailNextWith = Error.Transient("META_UNREACHABLE", "fora do ar");
        var service = Service();

        var first = await service.HandleAsync(Samples.Text("oi"), default);
        var retry = await service.HandleAsync(Samples.Text("oi"), default);

        first.IsFailure.Should().BeTrue();
        retry.Value.Should().Be(InboundOutcome.Replied);
        _whatsApp.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task OnRetry_TheRecordedReplyKeepsTheSameExternalId_SoTheCoreDoesNotDuplicateIt()
    {
        _whatsApp.FailNextWith = Error.Transient("META_UNREACHABLE", "x");
        var service = Service();

        await service.HandleAsync(Samples.Text("oi"), default);
        await service.HandleAsync(Samples.Text("oi"), default);

        _core.Messages.Where(m => m.Item1 == MessageDirection.Outbound).Select(m => m.Item4)
            .Should().OnlyContain(id => id == "bot:wamid.IN1");
    }

    [Fact]
    public async Task AnExceptionMidFlow_ReleasesTheClaim_AndPropagates()
    {
        var service = new ConversationService(
            new FakeChannelRegistry(Samples.Channel()), new ThrowingCore(), _whatsApp, _ledger, NullLogger<ConversationService>.Instance);

        var act = async () => await service.HandleAsync(Samples.Text("oi"), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _ledger.Operations.Should().Contain("release:in:wamid.IN1");
    }

    // ---- multi-escritório ----

    [Fact]
    public async Task AMessageToAnUnknownNumber_IsIgnored_WithoutTouchingTheCore()
    {
        var message = Samples.Text("oi") with { PhoneNumberId = "NUMERO-DESCONHECIDO" };

        var result = await Service().HandleAsync(message, default);

        result.Value.Should().Be(InboundOutcome.UnknownChannel);
        _core.Calls.Should().BeEmpty();
        _whatsApp.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task EachChannelUsesItsOwnOfficeName()
    {
        var other = Samples.Channel();
        other.PhoneNumberId = "PNID-2";
        other.OfficeName = "Outro Escritório";

        await Service(Samples.Channel(), other).HandleAsync(Samples.Text("oi") with { PhoneNumberId = "PNID-2" }, default);

        _whatsApp.Sent.Single().Body.Should().Contain("Outro Escritório");
    }

    // ---- doubles específicos destes cenários ----

    private sealed class StaleStartCore(FakeCore inner) : ICoreApiClient
    {
        public async Task<Result<AtendimentoRef>> StartAtendimentoAsync(WhatsAppChannel c, string p, string? n, CancellationToken t)
        {
            var started = await inner.StartAtendimentoAsync(c, p, n, t);
            inner.Status = AtendimentoStatuses.Human; // o advogado assume logo depois do "start"
            return started;
        }

        public Task<Result<string>> GetStatusAsync(WhatsAppChannel c, Guid id, CancellationToken t) => inner.GetStatusAsync(c, id, t);

        public Task<Result<Guid>> SendMessageAsync(WhatsAppChannel c, Guid id, MessageDirection d, MessageSender s, string b, string? e, CancellationToken t) =>
            inner.SendMessageAsync(c, id, d, s, b, e, t);

        public Task<Result> RequestHandoffAsync(WhatsAppChannel c, Guid id, CancellationToken t) => inner.RequestHandoffAsync(c, id, t);
    }

    private sealed class FailingHandoffCore(FakeCore inner) : ICoreApiClient
    {
        public Task<Result<AtendimentoRef>> StartAtendimentoAsync(WhatsAppChannel c, string p, string? n, CancellationToken t) => inner.StartAtendimentoAsync(c, p, n, t);

        public Task<Result<string>> GetStatusAsync(WhatsAppChannel c, Guid id, CancellationToken t) => inner.GetStatusAsync(c, id, t);

        public Task<Result<Guid>> SendMessageAsync(WhatsAppChannel c, Guid id, MessageDirection d, MessageSender s, string b, string? e, CancellationToken t) =>
            inner.SendMessageAsync(c, id, d, s, b, e, t);

        public Task<Result> RequestHandoffAsync(WhatsAppChannel c, Guid id, CancellationToken t) =>
            Task.FromResult(Result.Failure(Error.Transient("CORE_HTTP_503", "indisponível")));
    }

    private sealed class ThrowingCore : ICoreApiClient
    {
        public Task<Result<AtendimentoRef>> StartAtendimentoAsync(WhatsAppChannel c, string p, string? n, CancellationToken t) =>
            throw new InvalidOperationException("boom");

        public Task<Result<string>> GetStatusAsync(WhatsAppChannel c, Guid id, CancellationToken t) => throw new InvalidOperationException();

        public Task<Result<Guid>> SendMessageAsync(WhatsAppChannel c, Guid id, MessageDirection d, MessageSender s, string b, string? e, CancellationToken t) =>
            throw new InvalidOperationException();

        public Task<Result> RequestHandoffAsync(WhatsAppChannel c, Guid id, CancellationToken t) => throw new InvalidOperationException();
    }

    private sealed class OrderTrackingCore(FakeCore inner, List<string> order) : ICoreApiClient
    {
        public Task<Result<AtendimentoRef>> StartAtendimentoAsync(WhatsAppChannel c, string p, string? n, CancellationToken t) => inner.StartAtendimentoAsync(c, p, n, t);

        public Task<Result<string>> GetStatusAsync(WhatsAppChannel c, Guid id, CancellationToken t)
        {
            order.Add("core:status");
            return inner.GetStatusAsync(c, id, t);
        }

        public Task<Result<Guid>> SendMessageAsync(WhatsAppChannel c, Guid id, MessageDirection d, MessageSender s, string b, string? e, CancellationToken t)
        {
            order.Add($"core:{d}");
            return inner.SendMessageAsync(c, id, d, s, b, e, t);
        }

        public Task<Result> RequestHandoffAsync(WhatsAppChannel c, Guid id, CancellationToken t) => inner.RequestHandoffAsync(c, id, t);
    }

    private sealed class OrderTrackingWhatsApp(FakeWhatsApp inner, List<string> order) : IWhatsAppClient
    {
        public Task<Result<string>> SendTextAsync(WhatsAppChannel channel, string to, string body, CancellationToken t)
        {
            order.Add("whatsapp");
            return inner.SendTextAsync(channel, to, body, t);
        }
    }
}
