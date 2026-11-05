namespace Advocacia.Bot.Application;

/// <summary>
/// Erro de uma chamada externa (Core ou Meta). <see cref="IsTransient"/> separa o que vale
/// repetir (rede, 5xx, 429) do que é definitivo (4xx de regra): o webhook responde 500 à Meta
/// só no primeiro caso, para ela reentregar; no segundo, repetir não adiantaria.
/// </summary>
public sealed record Error(string Code, string Message, bool IsTransient = false)
{
    public static Error Transient(string code, string message) => new(code, message, IsTransient: true);

    public static Error Permanent(string code, string message) => new(code, message);
}

public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        ErrorValue = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    private Error? ErrorValue { get; }

    public Error Error => ErrorValue ?? throw new InvalidOperationException("Result de sucesso não tem erro.");

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Failure<T>(Error error) => new(default, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, Error? error)
        : base(error is null, error)
    {
        _value = value;
    }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result de falha não tem valor.");
}
