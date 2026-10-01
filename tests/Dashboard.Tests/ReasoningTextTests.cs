using AzureFinOps.Dashboard.AI.Runtime;

namespace Dashboard.Tests;

public sealed class ReasoningTextTests
{
    [Fact]
    public void ANewSummaryPartStartsItsOwnParagraph()
    {
        var thinking = new AgentConversation.ReasoningText();
        Assert.Equal("**Checking prices**", thinking.Append("**Checking prices**", ("rs_1", 0)));
        Assert.Equal("\n\nI compared the", thinking.Append("\n\nI compared the", ("rs_1", 0)));
        Assert.Equal(" requirements", thinking.Append(" requirements", ("rs_1", 0)));
        Assert.Equal(".", thinking.Append(".", ("rs_1", 0)));
        Assert.Equal("\n\n**Finalizing the calculation**", thinking.Append("**Finalizing the calculation**", ("rs_1", 1)));

        Assert.Equal("**Checking prices**\n\nI compared the requirements.\n\n**Finalizing the calculation**", thinking.Take());
        Assert.Equal(string.Empty, thinking.Take());
    }

    [Fact]
    public void ExistingLineBreaksAndUnknownPartsAreKept()
    {
        var thinking = new AgentConversation.ReasoningText();
        thinking.Append("First part.\n", ("rs_1", 0));
        Assert.Equal("\n**Second**", thinking.Append("**Second**", ("rs_1", 1)));
        Assert.Equal("**", thinking.Append("**", null));
        Assert.Equal("First part.\n\n**Second****", thinking.Take());

        // A part after a tool call is still separated from what was streamed before it.
        thinking.Append("Done.", ("rs_1", 1));
        thinking.Take();
        Assert.Equal("\n\n**Next step**", thinking.Append("**Next step**", ("rs_2", 0)));
        Assert.Equal("**Next step**", thinking.Take());
    }
}
