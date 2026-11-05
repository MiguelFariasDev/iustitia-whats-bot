using System.Text;
using Advocacia.Bot.Application.Webhooks;
using FluentAssertions;

namespace Advocacia.Bot.UnitTests;

public class MetaPayloadParserTests
{
    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    private const string TextMessage = """
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "id": "WABA-1",
            "changes": [{
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "558533334444", "phone_number_id": "PNID-1" },
                "contacts": [{ "profile": { "name": "Maria Souza" }, "wa_id": "5585999998888" }],
                "messages": [{
                  "from": "5585999998888", "id": "wamid.ABC", "timestamp": "1730000000",
                  "text": { "body": "Olá, doutora" }, "type": "text"
                }]
              }
            }]
          }]
        }
        """;

    [Fact]
    public void ExtractsATextMessageWithItsContactName()
    {
        var messages = MetaPayloadParser.Parse(Json(TextMessage));

        messages.Should().ContainSingle().Which.Should().BeEquivalentTo(new InboundMessage(
            "wamid.ABC", "PNID-1", "5585999998888", "Maria Souza", "Olá, doutora", "text"));
    }

    [Fact]
    public void KeepsMediaMessagesAsNonText()
    {
        var json = TextMessage
            .Replace("\"text\": { \"body\": \"Olá, doutora\" }, \"type\": \"text\"", "\"image\": { \"id\": \"MEDIA-1\" }, \"type\": \"image\"");

        var message = MetaPayloadParser.Parse(Json(json)).Single();

        message.Type.Should().Be("image");
        message.Text.Should().BeNull();
        message.IsText.Should().BeFalse();
    }

    [Fact]
    public void IgnoresDeliveryStatusNotifications()
    {
        const string statuses = """
            { "entry": [{ "changes": [{ "value": {
                "metadata": { "phone_number_id": "PNID-1" },
                "statuses": [{ "id": "wamid.OUT", "status": "delivered", "recipient_id": "5585999998888" }]
            } }] }] }
            """;

        MetaPayloadParser.Parse(Json(statuses)).Should().BeEmpty();
    }

    [Fact]
    public void ExtractsEveryMessageOfABatch_InOrder()
    {
        const string batch = """
            { "entry": [{ "changes": [{ "value": {
                "metadata": { "phone_number_id": "PNID-1" },
                "messages": [
                  { "from": "5585911112222", "id": "wamid.1", "type": "text", "text": { "body": "um" } },
                  { "from": "5585911112222", "id": "wamid.2", "type": "text", "text": { "body": "dois" } }
                ]
            } }] }] }
            """;

        MetaPayloadParser.Parse(Json(batch)).Select(m => m.Text).Should().Equal("um", "dois");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"entry": []}""")]
    [InlineData("""{"entry": [{}]}""")]
    [InlineData("""{"entry": [{"changes": [{}]}]}""")]
    [InlineData("""{"entry": [{"changes": [{"value": {"messages": []}}]}]}""")]
    [InlineData("""{"entry": [{"changes": [{"value": {"metadata": {}, "messages": [{"id": "x"}]}}]}]}""")]
    public void ToleratesIncompletePayloads(string json)
    {
        MetaPayloadParser.Parse(Json(json)).Should().BeEmpty();
    }

    [Fact]
    public void ThrowsOnInvalidJson_SoTheCallerCanDecide()
    {
        var act = () => MetaPayloadParser.Parse(Json("{ nao é json"));

        act.Should().Throw<System.Text.Json.JsonException>();
    }
}
