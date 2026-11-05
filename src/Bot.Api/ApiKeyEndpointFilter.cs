using System.Security.Cryptography;
using System.Text;
using Advocacia.Bot.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Advocacia.Bot.Api;

/// <summary>
/// Autentica o Core no <c>/api/v1/bot/send</c> pelo header <c>X-Api-Key</c>. Compara os HASHES em
/// tempo constante (o que também iguala os tamanhos, sem vazar o comprimento da chave) e falha
/// fechada: chave não configurada rejeita tudo.
/// </summary>
public sealed class ApiKeyEndpointFilter(IOptionsMonitor<InboundApiOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.CurrentValue.ApiKey;

        if (string.IsNullOrWhiteSpace(expected)
            || !context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided)
            || !FixedTimeEquals(provided.ToString(), expected))
        {
            return ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "BOT_UNAUTHORIZED",
                extensions: new Dictionary<string, object?> { ["errorCode"] = "BOT_UNAUTHORIZED" }));
        }

        return next(context);
    }

    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
