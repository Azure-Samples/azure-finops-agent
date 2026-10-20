using AzureFinOps.Dashboard.AI.Runtime;

namespace Dashboard.Tests;

public sealed class ConversationCompactionTests
{
    [Theory]
    [InlineData(null, true, false, false)]
    [InlineData(AgentConversation.CompactAfterInputTokens, true, false, false)]
    [InlineData(AgentConversation.CompactAfterInputTokens + 1, true, false, true)]
    [InlineData(AgentConversation.CompactAfterInputTokens + 1, false, false, false)]
    [InlineData(AgentConversation.CompactAfterInputTokens + 1, true, true, false)]
    public void CompactsOnlyALargeChainWithoutPendingApprovals(long? tokens, bool hasContext, bool pendingApprovals, bool expected) =>
        Assert.Equal(expected, AgentConversation.ShouldCompact(tokens, hasContext, pendingApprovals));

    [Fact]
    public void RecapCarriesVisibleExchangesWithoutContextOrEarlierToolResults()
    {
        var recap = AgentConversation.Recap(
        [
            new UserMessageEvent("[CONTEXT: User IS connected to Azure. Today is 2026-01-01 (UTC).]\nWhat did Standard_D4s_v5 cost?"),
            new ReasoningEvent("private thinking"),
            new ToolStartEvent("call-1", "QueryAzure", "{\"url\":\"https://management.azure.com/synthetic\"}"),
            new ToolCompleteEvent("call-1", true, "{\"rows\":[[\"tool-only-value\"]]}", null),
            new AssistantMessageEvent("m1", "Checking the cost."),
            new AssistantMessageEvent("m2", "Standard_D4s_v5 cost **USD 12.34**."),
            new UserMessageEvent("<skill-context>injected</skill-context>"),
            new TurnIdleEvent(),
            new UserMessageEvent("Chart it by day"),
        ]);

        Assert.NotNull(recap);
        Assert.StartsWith("[CONVERSATION SO FAR:", recap);
        Assert.Contains("User: What did Standard_D4s_v5 cost?", recap);
        Assert.Contains("Assistant: Checking the cost.\n\nStandard_D4s_v5 cost **USD 12.34**.", recap);
        Assert.Contains("User: Chart it by day", recap);
        Assert.DoesNotContain("[CONTEXT", recap);
        Assert.DoesNotContain("skill-context", recap);
        Assert.DoesNotContain("tool-only-value", recap);
        Assert.DoesNotContain("private thinking", recap);
        Assert.EndsWith("The user's new message follows.]", recap);
    }

    [Fact]
    public void RecapKeepsTheNewestMessagesWithinItsBudget()
    {
        var events = new List<AgentEvent>();
        for (var i = 0; i < 20; i++)
        {
            events.Add(new UserMessageEvent($"Question {i}"));
            events.Add(new AssistantMessageEvent($"m{i}", $"Answer {i} " + new string('x', AgentConversation.RecapMessageCharacters * 2)));
        }

        var recap = AgentConversation.Recap(events)!;

        Assert.Contains("Answer 19 ", recap);
        Assert.Contains("Question 19", recap);
        Assert.DoesNotContain("Question 0\n", recap);
        Assert.Contains("earlier messages omitted", recap);
        Assert.Contains("…[truncated]", recap);
        Assert.True(recap.Length < AgentConversation.RecapCharacters + 1_000);
    }

    [Fact]
    public void RecapIsNullWithoutVisibleMessages() =>
        Assert.Null(AgentConversation.Recap([new UserMessageEvent("[CONTEXT: only context]"), new TurnIdleEvent()]));

    // An export offered under a pricing answer ran after a reset that kept only the answer's text; the workbook marked unit
    // rates "unavailable" that the turn before had read.
    [Fact]
    public void RecapCarriesWhatTheLastTurnsEvidenceToolsReturned()
    {
        var recap = AgentConversation.Recap(
        [
            new UserMessageEvent("Earlier question"),
            new ToolStartEvent("old", "QueryAzure", "{\"url\":\"https://prices.azure.com/old\"}"),
            new ToolCompleteEvent("old", true, "HTTP 200 OK\nolder-turn-value", null),
            new AssistantMessageEvent("m0", "Earlier answer."),
            new UserMessageEvent("[CONTEXT: connected]\nPrice the 23 items"),
            new ToolStartEvent("a", "QueryAzure", "{\"url\":\"https://prices.azure.com/api/retail/prices?$filter=a\",\"query\":\"Items.Select(i => i.retailPrice)\"}"),
            new ToolCompleteEvent("a", true, "HTTP 200 OK\nQuery result: [0.005,1.75]", null),
            new ToolStartEvent("u", "QueryUploadedFile", "{\"fileId\":\"0123456789ab\",\"mode\":\"head\"}"),
            new ToolCompleteEvent("u", true, "{\"ok\":true,\"rows\":[[\"Firewall\",1]]}", null),
            new ToolStartEvent("d", "microsoft_docs_search", "{\"query\":\"pricing\"}"),
            new ToolCompleteEvent("d", true, "docs-only-value", null),
            new ToolStartEvent("w", "web_search", null),
            new ToolCompleteEvent("w", true, "Web search completed.", null),
            new ToolStartEvent("f", "QueryAzure", "{\"url\":\"https://management.azure.com/failed\"}"),
            new ToolCompleteEvent("f", true, "HTTP 403 Forbidden\nforbidden-value", null),
            new ToolStartEvent("e", "QueryAzure", "{\"query\":\"bad\"}"),
            new ToolCompleteEvent("e", true, "Error: the calculation failed: error-value", null),
            new ToolStartEvent("p", "QueryUploadedFile", "{\"fileId\":\"0123456789ab\",\"mode\":\"query\"}"),
            new ToolCompleteEvent("p", true, "{\"ok\": false, \"error\": \"mode 'query' not supported\", \"kind\": \"pdf\"}", null),
            new ToolStartEvent("q", "QueryUploadedFile", "{\"fileId\":\"0123456789ab\",\"mode\":\"count\"}"),
            new ToolCompleteEvent("q", true, "{\"ok\":false,\"error\":\"file_inspect timed out after 30s\"}", null),
            new ToolStartEvent("x", "GenerateDataReport", "{\"format\":\"xlsx\"}"),
            new ToolCompleteEvent("x", true, "__HTML_READY__:a2:Prices.xlsx:12 rows (XLSX)", null),
            new AssistantMessageEvent("m1", "The estimate is USD 18,400."),
            new TurnIdleEvent(),
        ])!;

        Assert.Contains("What the last turn's tool calls returned, oldest first", recap);
        Assert.Contains("- QueryAzure {\"url\":\"https://prices.azure.com/api/retail/prices?$filter=a\"", recap);
        Assert.Contains("Query result: [0.005,1.75]", recap);
        Assert.Contains("- QueryUploadedFile {\"fileId\":\"0123456789ab\",\"mode\":\"head\"}\n{\"ok\":true,\"rows\":[[\"Firewall\",1]]}", recap);
        Assert.True(recap.IndexOf("[0.005,1.75]", StringComparison.Ordinal) < recap.IndexOf("Firewall", StringComparison.Ordinal));
        foreach (var excluded in new[] { "older-turn-value", "docs-only-value", "Web search completed", "forbidden-value", "error-value", "__HTML_READY__",
            "not supported", "timed out" })
            Assert.DoesNotContain(excluded, recap);
        Assert.Contains("a value missing here is not unavailable", recap);
        Assert.EndsWith("The user's new message follows.]", recap);
    }

    [Fact]
    public void RecapEvidenceIsBoundedAndKeepsTheNewestResults()
    {
        var events = new List<AgentEvent> { new UserMessageEvent("Compare every region") };
        for (var i = 0; i < 12; i++)
        {
            events.Add(new ToolStartEvent($"c{i}", "QueryAzure", $"{{\"url\":\"https://prices.azure.com/region-{i}\"}}"));
            events.Add(new ToolCompleteEvent($"c{i}", true, $"HTTP 200 OK\nregion-{i} " + new string('r', AgentConversation.RecapEvidenceResultCharacters * 2), null));
        }
        events.Add(new TurnIdleEvent());

        var recap = AgentConversation.Recap(events)!;

        Assert.Contains("region-11 ", recap);
        Assert.DoesNotContain("region-0 ", recap);
        Assert.Contains("earlier calls omitted", recap);
        Assert.Contains("…[cut]", recap);
        Assert.True(recap.Length < AgentConversation.RecapEvidenceCharacters + AgentConversation.RecapCharacters + 2_000);
        Assert.True(recap.IndexOf("region-9 ", StringComparison.Ordinal) < recap.IndexOf("region-11 ", StringComparison.Ordinal));
    }

    [Fact]
    public void RecapHasNoEvidenceSectionWhenTheLastTurnReadNothing()
    {
        var recap = AgentConversation.Recap(
        [
            new UserMessageEvent("Price it"),
            new ToolStartEvent("a", "QueryAzure", "{\"url\":\"https://prices.azure.com/a\"}"),
            new ToolCompleteEvent("a", true, "HTTP 200 OK\nevidence-from-an-earlier-turn", null),
            new AssistantMessageEvent("m1", "Done."),
            new UserMessageEvent("Thanks, one question: which region is cheapest?"),
            new AssistantMessageEvent("m2", "East US."),
            new TurnIdleEvent(),
        ])!;

        Assert.DoesNotContain("What the last turn's tool calls returned", recap);
        Assert.DoesNotContain("evidence-from-an-earlier-turn", recap);
    }

    // A reset lost the exporter script the agent had written and the screenshot the user attached; the agent then asked
    // the user to upload its own script.
    [Fact]
    public void RecapNamesEarlierFilesAndCarriesTheLatestScriptCode()
    {
        var recap = AgentConversation.Recap(
        [
            new UserMessageEvent("Which VMs does this change affect?"),
            new ToolStartEvent("s1", "GenerateScript", "{\"language\":\"bash\",\"filename\":\"export\",\"scriptContent\":\"echo first\"}"),
            new ToolCompleteEvent("s1", true, "__SCRIPT_READY__:a1:export.sh:1:bash:Exporter", null),
            new ToolStartEvent("r1", "GenerateDataReport", "{\"format\":\"xlsx\"}"),
            new ToolCompleteEvent("r1", true, "__HTML_READY__:a2:Affected-VMs.xlsx:12 rows (XLSX)", null),
            new ToolStartEvent("s2", "GenerateScript", "{\"language\":\"bash\",\"filename\":\"export\",\"scriptContent\":\"echo second\"}"),
            new ToolCompleteEvent("s2", true, "__SCRIPT_READY__:a3:export-v2.sh:1:bash:Exporter", null),
            new ToolStartEvent("s3", "GenerateScript", "{\"scriptContent\":\"echo failed\"}"),
            new ToolCompleteEvent("s3", false, null, "Error: invalid"),
            new AssistantMessageEvent("m1", "The script and workbook are ready."),
        ], ["notice.png"])!;

        Assert.Contains("Files from earlier turns", recap);
        Assert.Contains("- export.sh (bash script, 1 lines)", recap);
        Assert.Contains("- Affected-VMs.xlsx", recap);
        Assert.Contains("- notice.png, an image the user attached (no longer visible to you", recap);
        Assert.Contains("Code of the latest script, export-v2.sh:\n```bash\necho second\n```", recap);
        Assert.DoesNotContain("echo first", recap);
        Assert.DoesNotContain("echo failed", recap);
        Assert.EndsWith("The user's new message follows.]", recap);
    }
}
