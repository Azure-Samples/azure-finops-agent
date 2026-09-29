using System.ClientModel.Primitives;
using AzureFinOps.Dashboard.AI.Runtime;
using OpenAI.Responses;

#pragma warning disable OPENAI001 // Experimental Responses item types, parsed exactly as the service returns them.

namespace Dashboard.Tests;

public class WebSearchOutcomeTests
{
    private static ResponseItem Item(string action) => ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(
        $$"""{"type":"web_search_call","id":"ws_1","status":"completed","action":{{action}}}"""))!;

    [Fact]
    public void SearchRecordsItsQueries() =>
        Assert.Equal("""Web search completed: searched ["azure region launch 2026"].""",
            AgentConversation.WebSearchOutcome(Item("""{"type":"search","query":"azure region launch 2026","queries":["azure region launch 2026"]}""")));

    [Fact]
    public void SearchWithOnlyTheSingleQueryFieldRecordsIt() =>
        Assert.Equal("""Web search completed: searched ["gpu news"].""",
            AgentConversation.WebSearchOutcome(Item("""{"type":"search","query":"gpu news"}""")));

    [Fact]
    public void OpenPageRecordsTheReadPageNotASearch() =>
        Assert.Equal("Web search completed: opened page https://example.com/news.",
            AgentConversation.WebSearchOutcome(Item("""{"type":"open_page","url":"https://example.com/news"}""")));

    [Fact]
    public void FindInPageRecordsThePatternAndPage() =>
        Assert.Equal("""Web search completed: found "D4s v5" in page https://example.com/pricing.""",
            AgentConversation.WebSearchOutcome(Item("""{"type":"find_in_page","url":"https://example.com/pricing","pattern":"D4s v5"}""")));

    [Fact]
    public void UnknownOrMissingItemsKeepTheGenericOutcome()
    {
        Assert.Equal("Web search completed.", AgentConversation.WebSearchOutcome(null));
        Assert.Equal("Web search completed.", AgentConversation.WebSearchOutcome("not an item"));
    }
}
