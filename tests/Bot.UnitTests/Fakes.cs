using Advocacia.Bot.Application;
using Advocacia.Bot.Application.Abstractions;
using Advocacia.Bot.Application.Webhooks;

namespace Advocacia.Bot.UnitTests;

internal static class Samples
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static WhatsAppChannel Channel() => new()
    {
        TenantId = TenantId,
        PhoneNumberId = "PNID-1",
        AccessToken = "meta-token",
        CoreApiKey = "adv_bot_core_key",
        OfficeName = "Escritório Teste",
    };

    public static InboundMessage Text(string text, string id = "wamid.IN1", string from = "5585999998888") =>
        new(id, "PNID-1", from, "Maria", text, "text");

    public static InboundMessage Media(string id = "wamid.IN1") =>
        new(id, "PNID-1", "5585999998888", "Maria", null, "image");
}

internal sealed class FakeChannelRegistry(params WhatsAppChannel[] channels) : IChannelRegistry
{
    public WhatsAppChannel? FindByPhoneNumberId(string phoneNumberId) =>
        channels.FirstOrDefault(channel => channel.PhoneNumberId == phoneNumberId);

    public WhatsAppChannel? FindByTenantId(Guid tenantId) =>
        channels.FirstOrDefault(channel => channel.TenantId == tenantId);
}

/// <summary>
/// Simula o Core o suficiente para testar o Bot: guarda o status, aplica a regra "o bot não fala
/// com um humano" e registra a ORDEM das chamadas (a ordem é parte do contrato do handoff).
/// </summary>
internal sealed class FakeCore : ICoreApiClient
{
    public string Status { get; set; } = AtendimentoStatuses.Bot;

    public bool CreatedNewAtendimento { get; set; } = true;

    public Guid AtendimentoId { get; } = Guid.NewGuid();

    public List<string> Calls { get; } = [];

    public List<(MessageDirection Direction, MessageSender Sender, string Body, string? ExternalId)> Messages { get; } = [];

    /// <summary>Quando definido, a próxima chamada ao Core falha com este erro.</summary>
    public Error? FailNextWith { get; set; }

    /// <summary>Simula o advogado assumindo no instante entre a consulta de status e a resposta do bot.</summary>
    public bool LawyerTakesOverBeforeBotReplies { get; set; }

    private Result<T>? MaybeFail<T>()
    {
        if (FailNextWith is { } error)
        {
            FailNextWith = null;
            return Result.Failure<T>(error);
        }

        return null;
    }

    public Task<Result<AtendimentoRef>> StartAtendimentoAsync(
        WhatsAppChannel channel, string phoneNumber, string? contactName, CancellationToken cancellationToken)
    {
        Calls.Add("start");
        return Task.FromResult(MaybeFail<AtendimentoRef>()
            ?? Result.Success(new AtendimentoRef(AtendimentoId, Status, CreatedNewAtendimento)));
    }

    public Task<Result<string>> GetStatusAsync(WhatsAppChannel channel, Guid atendimentoId, CancellationToken cancellationToken)
    {
        Calls.Add("status");
        return Task.FromResult(MaybeFail<string>() ?? Result.Success(Status));
    }

    public Task<Result<Guid>> SendMessageAsync(
        WhatsAppChannel channel,
        Guid atendimentoId,
        MessageDirection direction,
        MessageSender sender,
        string body,
        string? externalId,
        CancellationToken cancellationToken)
    {
        Calls.Add($"message:{direction}:{sender}");

        if (MaybeFail<Guid>() is { } failure)
        {
            return Task.FromResult(failure);
        }

        if (direction == MessageDirection.Outbound && LawyerTakesOverBeforeBotReplies)
        {
            Status = AtendimentoStatuses.Human;
        }

        // A regra do Core: o bot NUNCA fala com uma conversa em Human.
        if (sender == MessageSender.Bot && Status == AtendimentoStatuses.Human)
        {
            return Task.FromResult(Result.Failure<Guid>(Error.Permanent(CoreErrorCodes.BotBlocked, "bloqueado")));
        }

        Messages.Add((direction, sender, body, externalId));
        return Task.FromResult(Result.Success(Guid.NewGuid()));
    }

    public Task<Result> RequestHandoffAsync(WhatsAppChannel channel, Guid atendimentoId, CancellationToken cancellationToken)
    {
        Calls.Add("handoff");

        if (FailNextWith is { } error)
        {
            FailNextWith = null;
            return Task.FromResult(Result.Failure(error));
        }

        if (Status == AtendimentoStatuses.Bot)
        {
            Status = AtendimentoStatuses.PendingHuman;
        }

        return Task.FromResult(Result.Success());
    }
}

internal sealed class FakeWhatsApp : IWhatsAppClient
{
    public List<(string To, string Body)> Sent { get; } = [];

    public Error? FailNextWith { get; set; }

    public int Counter { get; private set; }

    public Task<Result<string>> SendTextAsync(
        WhatsAppChannel channel, string toPhoneNumber, string body, CancellationToken cancellationToken)
    {
        if (FailNextWith is { } error)
        {
            FailNextWith = null;
            return Task.FromResult(Result.Failure<string>(error));
        }

        Sent.Add((toPhoneNumber, body));
        return Task.FromResult(Result.Success($"wamid.OUT{++Counter}"));
    }
}

internal sealed class InMemoryLedger : IIdempotencyLedger
{
    private readonly Dictionary<string, (bool Done, string? Result)> _records = [];

    public List<string> Operations { get; } = [];

    public Task<ClaimResult> ClaimAsync(string key, CancellationToken cancellationToken)
    {
        Operations.Add($"claim:{key}");

        if (!_records.TryGetValue(key, out var existing))
        {
            _records[key] = (false, null);
            return Task.FromResult(new ClaimResult(ClaimOutcome.Claimed));
        }

        return Task.FromResult(existing.Done
            ? new ClaimResult(ClaimOutcome.AlreadyDone, existing.Result)
            : new ClaimResult(ClaimOutcome.InProgress));
    }

    public Task CompleteAsync(string key, string? result, CancellationToken cancellationToken)
    {
        Operations.Add($"complete:{key}");
        _records[key] = (true, result);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        Operations.Add($"release:{key}");
        _records.Remove(key);
        return Task.CompletedTask;
    }
}
