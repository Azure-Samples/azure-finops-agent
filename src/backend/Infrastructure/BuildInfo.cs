using System.Diagnostics;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// The running build: commit, build number and branch baked into the image by CI, or read from git
/// in a local checkout. Resolved once, because the local fallback starts git processes.
/// </summary>
public static class BuildInfo
{
    private static readonly Lazy<(string Sha, string Number, string Branch)> Resolved = new(Resolve);

    public static string Sha => Resolved.Value.Sha;
    public static string Number => Resolved.Value.Number;
    public static string Branch => Resolved.Value.Branch;

    /// <summary>"main 158 (abc1234)", for places that name the build in one phrase.</summary>
    public static string Describe() => $"{Branch} {Number} ({Sha})";

    private static (string, string, string) Resolve() => (
        Environment.GetEnvironmentVariable("BUILD_SHA") is { Length: > 0 } sha ? sha : Git("rev-parse --short HEAD", "dev"),
        Environment.GetEnvironmentVariable("BUILD_NUMBER") is { Length: > 0 } number ? number : Git("rev-list --count HEAD", "0"),
        Environment.GetEnvironmentVariable("BUILD_BRANCH") is { Length: > 0 } branch ? branch : Git("rev-parse --abbrev-ref HEAD", "main"));

    private static string Git(string arguments, string fallback)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            return output.Length > 0 ? output : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
