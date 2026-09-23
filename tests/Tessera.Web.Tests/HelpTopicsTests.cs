using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Help;
using Tessera.Core.Resources;
using Tessera.Web.Services;

namespace Tessera.Web.Tests;

// A real IStringLocalizer<Messages>, not a fake — GetQuestion/GetAnswer build resx keys from
// $"Help.{topic}" by string interpolation, so a missing or misspelled key for any HelpTopic
// would otherwise go unnoticed until a real user hit it.
public class HelpTopicsTests
{
    private readonly IStringLocalizer<Messages> localizer =
        new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider().GetRequiredService<IStringLocalizer<Messages>>();

    public static TheoryData<HelpTopic> AllTopics() => new(HelpTopics.All);

    [Theory]
    [MemberData(nameof(AllTopics))]
    public void GetQuestion_ReturnsRealResxText_ForEveryTopic(HelpTopic topic)
    {
        var question = HelpTopics.GetQuestion(topic, localizer);

        Assert.False(string.IsNullOrWhiteSpace(question));
        Assert.DoesNotContain($"Help.{topic}.Question", question); // resx miss returns the key itself
    }

    [Theory]
    [MemberData(nameof(AllTopics))]
    public void GetAnswer_ReturnsRealResxText_ForEveryTopic(HelpTopic topic)
    {
        var answer = HelpTopics.GetAnswer(topic, localizer);

        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.DoesNotContain($"Help.{topic}.Answer", answer);
    }

    [Fact]
    public void GetRoutePattern_PointsAtTheRealCalendarLinkingPage()
    {
        var spaceId = Guid.NewGuid();
        var pattern = HelpTopics.GetRoutePattern(HelpTopic.Calendar);

        Assert.NotNull(pattern);
        Assert.Equal($"/spaces/{spaceId}/calendars", string.Format(pattern, spaceId));
    }

    // These three are answered entirely in chat (undo, voice, the digest) — nothing on the web
    // console does the equivalent, so there's deliberately nothing to link to.
    [Theory]
    [InlineData(HelpTopic.Undo)]
    [InlineData(HelpTopic.Voice)]
    [InlineData(HelpTopic.Digest)]
    public void GetRoutePattern_IsNull_ForChatOnlyTopics(HelpTopic topic)
    {
        Assert.Null(HelpTopics.GetRoutePattern(topic));
    }
}
