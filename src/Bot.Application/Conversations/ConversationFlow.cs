using System.Globalization;
using System.Text;

namespace Advocacia.Bot.Application.Conversations;

public abstract record FlowDecision
{
    /// <summary>Responder com um texto do fluxo automático.</summary>
    public sealed record Reply(string Text) : FlowDecision;

    /// <summary>O cliente pediu um humano: pedir o handoff ao Core e avisar o cliente.</summary>
    public sealed record Handoff : FlowDecision;
}

/// <summary>
/// O menu automático. Puro (sem I/O), para o comportamento ser testável sem Core nem Meta.
///
/// Transparência (princípio 5 do projeto, Recomendação nº 001/2024 da OAB): o primeiro contato
/// informa que o atendimento é automatizado e que o cliente pode falar com uma pessoa.
/// </summary>
public static class ConversationFlow
{
    /// <summary>Exatamente o texto exigido pelo fluxo de handoff.</summary>
    public const string HandoffNotice = "Aguarde, um advogado vai te atender.";

    public const string MediaNotSupportedNotice =
        "No momento só consigo ler mensagens de texto. Digite *2* para falar com um advogado.";

    private static readonly string[] HumanKeywords = ["humano", "atendente", "advogado", "advogada"];

    public static FlowDecision Decide(string? text, bool firstContact, string officeName)
    {
        var normalized = Normalize(text);

        if (normalized == "2" || HumanKeywords.Any(keyword => ContainsWord(normalized, keyword)))
        {
            return new FlowDecision.Handoff();
        }

        if (normalized == "1")
        {
            return new FlowDecision.Reply(
                "Para acompanhar seu processo, um advogado precisa confirmar sua identidade. " +
                "Digite *2* para falar com um advogado ou *0* para voltar ao menu.");
        }

        return new FlowDecision.Reply(Menu(officeName, firstContact));
    }

    public static string Menu(string officeName, bool firstContact)
    {
        var greeting = firstContact
            ? $"Olá! Você está falando com o atendimento automatizado de {officeName}. " +
              "Você pode pedir a qualquer momento para falar com uma pessoa.\n\n"
            : string.Empty;

        return greeting +
               "Como posso ajudar? Digite:\n" +
               "*1* - Acompanhar meu processo\n" +
               "*2* - Falar com um advogado";
    }

    /// <summary>Minúsculas, sem acento, sem pontuação nas pontas — "Humano!" e "humano" são o mesmo pedido.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim(' ', '.', '!', '?', ',', ';', ':', '*');
    }

    private static bool ContainsWord(string normalized, string word) =>
        normalized.Split([' ', '.', ',', '!', '?', ';', ':', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains(word);
}
