using System.Text;
using Advocacia.Bot.Application.Webhooks;
using FluentAssertions;

namespace Advocacia.Bot.UnitTests;

public class MetaSignatureValidatorTests
{
    private const string Secret = "app-secret-da-meta";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[]}""");

    [Fact]
    public void AcceptsTheSignatureTheMetaWouldSend()
    {
        var header = MetaSignatureValidator.Sign(Body, Secret);

        MetaSignatureValidator.IsValid(Body, header, Secret).Should().BeTrue();
    }

    [Fact]
    public void MatchesAKnownVector_NotJustItsOwnImplementation()
    {
        // HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog") — vetor público da RFC 4231 / Wikipedia.
        var body = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");

        MetaSignatureValidator.IsValid(
                body, "sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", "key")
            .Should().BeTrue();
    }

    [Fact]
    public void RejectsABodyAlteredByEvenOneByte()
    {
        var header = MetaSignatureValidator.Sign(Body, Secret);
        var tampered = (byte[])Body.Clone();
        tampered[10] ^= 0x01;

        MetaSignatureValidator.IsValid(tampered, header, Secret).Should().BeFalse();
    }

    [Fact]
    public void RejectsASignatureMadeWithAnotherSecret()
    {
        var header = MetaSignatureValidator.Sign(Body, "outro-segredo");

        MetaSignatureValidator.IsValid(Body, header, Secret).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256=")]
    [InlineData("sha256=zzzz-nao-e-hex")]
    [InlineData("sha1=abcdef")]
    [InlineData("abcdef")]
    [InlineData("sha256=abc")]
    public void RejectsMissingOrMalformedHeaders(string? header)
    {
        MetaSignatureValidator.IsValid(Body, header, Secret).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FailsClosed_WhenTheAppSecretIsNotConfigured(string? secret)
    {
        // Nem a "assinatura" calculada com segredo vazio pode abrir a porta.
        var header = MetaSignatureValidator.Sign(Body, string.Empty);

        MetaSignatureValidator.IsValid(Body, header, secret).Should().BeFalse();
    }

    [Fact]
    public void IsCaseInsensitiveOnThePrefixAndTheHex()
    {
        var header = MetaSignatureValidator.Sign(Body, Secret);
        var shouted = "SHA256=" + header["sha256=".Length..].ToUpperInvariant();

        MetaSignatureValidator.IsValid(Body, shouted, Secret).Should().BeTrue();
    }
}
