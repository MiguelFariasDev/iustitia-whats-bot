using System.Security.Cryptography;
using System.Text;

namespace Advocacia.Bot.Application.Webhooks;

/// <summary>
/// Valida o header <c>X-Hub-Signature-256</c> da Meta: <c>sha256=</c> + HMAC-SHA256 do corpo
/// BRUTO com o App Secret. É a única prova de que o POST veio da Meta — sem ela, qualquer um
/// que descubra a URL injeta mensagens "de clientes" no Core.
///
/// Regras: o HMAC é sobre os bytes EXATOS recebidos (reserializar o JSON mudaria o hash); a
/// comparação é em tempo constante; e a validação falha FECHADA — App Secret ausente rejeita
/// tudo, em vez de aceitar tudo.
/// </summary>
public static class MetaSignatureValidator
{
    private const string Prefix = "sha256=";

    public static bool IsValid(ReadOnlySpan<byte> body, string? signatureHeader, string? appSecret)
    {
        if (string.IsNullOrWhiteSpace(appSecret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHeader.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>Gera a assinatura que a Meta enviaria — usado pelos testes.</summary>
    public static string Sign(ReadOnlySpan<byte> body, string appSecret) =>
        Prefix + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();
}
