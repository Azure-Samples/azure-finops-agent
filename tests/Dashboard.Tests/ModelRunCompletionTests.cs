using AzureFinOps.Dashboard.AI.Runtime;
using Microsoft.Extensions.AI;

namespace Dashboard.Tests;

public sealed class ModelRunCompletionTests
{
    private static readonly UsageContent Usage = new(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 });

    [Fact]
    public void CompletedFinalResponseIsNotAFailure()
    {
        var run = new ModelRunCompletion();
        run.Observe([new TextContent("Hello")], null);
        run.Observe([Usage], ChatFinishReason.Stop);

        Assert.True(run.Completed);
        Assert.Null(run.Failure);
    }

    [Fact]
    public void ToolRoundsMustEndWithACompletedResponse()
    {
        var run = new ModelRunCompletion();
        run.Observe([new FunctionCallContent("call-1", "QueryAzure")], null);
        run.Observe([Usage], ChatFinishReason.ToolCalls);
        run.Observe([new FunctionResultContent("call-1", "{}")], null);
        run.Observe([new TextContent("Partial")], null);

        Assert.False(run.Completed);
        Assert.Contains("ended its response before completing it", run.Failure);

        run.Observe([Usage], ChatFinishReason.Stop);
        Assert.Null(run.Failure);
    }

    [Fact]
    public void HostedWebSearchThatNeverCompletesIsAFailure()
    {
        var run = new ModelRunCompletion();
        run.Observe([new WebSearchToolCallContent("ws-1")], null);

        Assert.NotNull(run.Failure);
    }

    [Fact]
    public void ServiceErrorIsReportedEvenAfterCompletion()
    {
        var run = new ModelRunCompletion();
        run.Observe([new ErrorContent("Unable to get resource information.") { ErrorCode = "server_error" }], null);
        run.Observe([Usage], ChatFinishReason.Stop);

        Assert.Equal("The model service returned an error: Unable to get resource information.", run.Failure);
    }

    [Fact]
    public void ApprovalRequestAfterACompletedResponseIsNotAFailure()
    {
        var run = new ModelRunCompletion();
        var call = new FunctionCallContent("call-2", "ApplyAzureChange");
        run.Observe([call], null);
        run.Observe([Usage], ChatFinishReason.ToolCalls);
        run.Observe([new ToolApprovalRequestContent("request-1", call)], null);

        Assert.Null(run.Failure);
    }
}
