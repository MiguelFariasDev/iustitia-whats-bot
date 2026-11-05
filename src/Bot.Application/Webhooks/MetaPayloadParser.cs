using System.Text.Json;

namespace Advocacia.Bot.Application.Webhooks;

/// <summary>
/// Extrai as mensagens de entrada do payload do webhook da Meta
/// (<c>entry[].changes[].value.messages[]</c>). Tolerante de propósito: a Meta evolui o payload e
/// manda também notificações de status (entregue/lido), que aqui são ignoradas.
/// </summary>
public static class MetaPayloadParser
{
    public static IReadOnlyList<InboundMessage> Parse(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var messages = new List<InboundMessage>();

        if (!document.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value))
                {
                    continue;
                }

                var phoneNumberId = StringAt(value, "metadata", "phone_number_id");
                if (phoneNumberId is null || !value.TryGetProperty("messages", out var rawMessages)
                    || rawMessages.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var names = ContactNames(value);

                foreach (var raw in rawMessages.EnumerateArray())
                {
                    var id = StringAt(raw, "id");
                    var from = StringAt(raw, "from");
                    if (id is null || from is null)
                    {
                        continue;
                    }

                    var type = StringAt(raw, "type") ?? "unknown";
                    var text = type == "text" ? StringAt(raw, "text", "body") : null;
                    names.TryGetValue(from, out var contactName);

                    messages.Add(new InboundMessage(id, phoneNumberId, from, contactName, text, type));
                }
            }
        }

        return messages;
    }

    private static Dictionary<string, string> ContactNames(JsonElement value)
    {
        var names = new Dictionary<string, string>();

        if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                var waId = StringAt(contact, "wa_id");
                var name = StringAt(contact, "profile", "name");
                if (waId is not null && name is not null)
                {
                    names[waId] = name;
                }
            }
        }

        return names;
    }

    private static string? StringAt(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
