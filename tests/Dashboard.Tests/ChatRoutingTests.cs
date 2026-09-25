using AzureFinOps.Dashboard.AI;

namespace Dashboard.Tests;

public sealed class ChatRoutingTests
{
    [Theory]
    [InlineData("hi", true)]
    [InlineData(" Hello! ", true)]
    [InlineData("bonjour", true)]
    [InlineData("hej", true)]
    [InlineData("", false)]
    [InlineData("thanks", false)]
    [InlineData("yes", false)]
    [InlineData("do it", false)]
    [InlineData("then do it for me", false)]
    [InlineData("give me the list", false)]
    [InlineData("generate a script", false)]
    [InlineData("sous forme de tableau", false)]
    [InlineData("reverifie le contenu", false)]
    [InlineData("comment mettre en place", false)]
    [InlineData("vérifie le calcul", false)]
    [InlineData("actually use West Europe", false)]
    [InlineData("lav en tabel", false)]
    [InlineData("no, use Central US", false)]
    [InlineData("help", false)]
    [InlineData("hello, compare these files", false)]
    [InlineData("how about h100?", false)]
    [InlineData("I already have a h100 deployed", false)]
    [InlineData("how about on spot you think I can try the other regions for bigger chance for not getting shut down like nwo", false)]
    public void OnlyStandaloneGreetingsSuppressTools(string prompt, bool expected) =>
        Assert.Equal(expected, ChatEndpoints.IsTrivialPrompt(prompt));
}
