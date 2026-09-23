using Tessera.Core.Help;

namespace Tessera.Core.Tests.Help;

public class HelpTopicCodesTests
{
    public static TheoryData<HelpTopic> AllTopics() => new(Enum.GetValues<HelpTopic>());

    // The round trip a live get_help tool call actually exercises: LlmTools.cs's schema offers
    // ToCode's output as the enum values, the model echoes one back, HandleGetHelpAsync feeds it
    // to FromCode. Every HelpTopic must survive that trip unchanged, or the tool would silently
    // stop answering for whichever one didn't.
    [Theory]
    [MemberData(nameof(AllTopics))]
    public void ToCode_ThenFromCode_RoundTripsToTheSameTopic(HelpTopic topic)
    {
        var code = HelpTopicCodes.ToCode(topic);

        Assert.Equal(topic, HelpTopicCodes.FromCode(code));
    }

    [Fact]
    public void FromCode_ReturnsNull_ForAnUnrecognizedCode()
    {
        Assert.Null(HelpTopicCodes.FromCode("not_a_real_topic"));
    }

    [Fact]
    public void FromCode_ReturnsNull_ForNull()
    {
        Assert.Null(HelpTopicCodes.FromCode(null));
    }

    // Explicitly not Enum.TryParse(code, ignoreCase: true, ...) on the C# names directly — that
    // silently fails on multi-word values like ShoppingList vs. "shopping_list" (PascalCase vs.
    // snake_case is not a case difference). This pins the intended snake_case wire format down.
    [Fact]
    public void ToCode_UsesSnakeCase_ForMultiWordTopics()
    {
        Assert.Equal("shopping_list", HelpTopicCodes.ToCode(HelpTopic.ShoppingList));
    }
}
