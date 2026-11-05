namespace Advocacia.Bot.Application.Abstractions;

public enum ClaimOutcome
{
    /// <summary>Este chamador é o dono da operação e deve executá-la.</summary>
    Claimed,

    /// <summary>A operação já foi concluída antes; o resultado guardado vem junto.</summary>
    AlreadyDone,

    /// <summary>Outra execução está em andamento. Não repita agora.</summary>
    InProgress,
}

public sealed record ClaimResult(ClaimOutcome Outcome, string? Result = null);

/// <summary>
/// Livro de idempotência do Bot, no banco PRÓPRIO dele. Dois usos:
/// <list type="bullet">
/// <item><c>in:{wamid}</c> — a Meta reentrega webhooks (e reentrega de novo se respondermos
/// erro); sem isto o cliente receberia a resposta duas vezes.</item>
/// <item><c>out:{messageId}</c> — o Core pode repetir <c>/send</c> ao não receber a resposta;
/// sem isto o cliente receberia a mensagem do advogado duas vezes.</item>
/// </list>
/// </summary>
public interface IIdempotencyLedger
{
    Task<ClaimResult> ClaimAsync(string key, CancellationToken cancellationToken);

    Task CompleteAsync(string key, string? result, CancellationToken cancellationToken);

    /// <summary>Libera a chave após falha, para a repetição poder tentar de novo.</summary>
    Task ReleaseAsync(string key, CancellationToken cancellationToken);
}
