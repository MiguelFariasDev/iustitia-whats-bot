using Advocacia.Bot.Application.Conversations;
using FluentAssertions;

namespace Advocacia.Bot.UnitTests;

public class ConversationFlowTests
{
    [Theory]
    [InlineData("2")]
    [InlineData(" 2 ")]
    [InlineData("2.")]
    [InlineData("*2*")]
    [InlineData("quero falar com um humano")]
    [InlineData("Atendente!")]
    [InlineData("preciso de um ADVOGADO")]
    [InlineData("Advogada, por favor")]
    public void AskingForAHuman_RequestsTheHandoff(string text)
    {
        ConversationFlow.Decide(text, firstContact: false, "Escritório X").Should().BeOfType<FlowDecision.Handoff>();
    }

    [Theory]
    [InlineData("oi")]
    [InlineData("0")]
    [InlineData("22")]
    [InlineData("12")]
    [InlineData("bom dia")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnythingElse_ShowsTheMenu(string? text)
    {
        var decision = ConversationFlow.Decide(text, firstContact: false, "Escritório X");

        decision.Should().BeOfType<FlowDecision.Reply>().Which.Text.Should().Contain("*2* - Falar com um advogado");
    }

    [Fact]
    public void OptionOne_ExplainsHowToFollowTheCase_AndOffersAHuman()
    {
        var decision = ConversationFlow.Decide("1", firstContact: false, "Escritório X");

        decision.Should().BeOfType<FlowDecision.Reply>().Which.Text.Should().Contain("*2*");
    }

    [Fact]
    public void TheFirstContact_DisclosesThatItIsAutomated_AndNamesTheOffice()
    {
        // Princípio 5 do projeto: transparência com o cliente final (OAB, Recomendação nº 001/2024).
        var text = ((FlowDecision.Reply)ConversationFlow.Decide("oi", firstContact: true, "Silva & Associados")).Text;

        text.Should().Contain("automatizado").And.Contain("Silva & Associados").And.Contain("falar com uma pessoa");
    }

    [Fact]
    public void LaterMessages_DoNotRepeatTheDisclosure()
    {
        var text = ((FlowDecision.Reply)ConversationFlow.Decide("oi", firstContact: false, "Silva & Associados")).Text;

        text.Should().NotContain("automatizado");
    }

    [Fact]
    public void TheHandoffNotice_IsExactlyWhatTheFlowSpecifies()
    {
        ConversationFlow.HandoffNotice.Should().Be("Aguarde, um advogado vai te atender.");
    }

    [Theory]
    [InlineData("Humano")]
    [InlineData("HUMANO!!")]
    [InlineData("hümáno")]
    public void AccentsAndCaseDoNotMatter(string text)
    {
        ConversationFlow.Decide(text, false, "X").Should().BeOfType<FlowDecision.Handoff>();
    }
}
