using System.Text.RegularExpressions;

namespace GSCLSP.Core.Tools;

public sealed partial record GscToolSource(string Owner, string Repo, string Branch)
{
    public static readonly GscToolSource Default = new("xensik", "gsc-tool", "dev");

    public string EngineDirectoryUrl =>
        $"https://raw.githubusercontent.com/{Owner}/{Repo}/refs/heads/{Branch}/src/gsc/engine/";

    public bool IsDefault => Equals(Default);

    public string DisplayName => $"{Owner}/{Repo}@{Branch}";

    public static bool TryParse(string? raw, out GscToolSource source)
    {
        source = Default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        var owner = segments[0];
        var repo = TrimDotGit(segments[1]);
        var branch = Default.Branch;

        if (segments.Length >= 4 && segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
        {
            branch = Uri.UnescapeDataString(string.Join("/", segments.Skip(3)));
        }
        else if (segments.Length >= 4 && segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            branch = Uri.UnescapeDataString(segments[3]);
        }

        if (!IsSafeSegment(owner) || !IsSafeSegment(repo) || !IsSafeBranch(branch))
            return false;

        source = new GscToolSource(owner, repo, branch);
        return true;
    }

    private static string TrimDotGit(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    private static bool IsSafeSegment(string s) =>
        !string.IsNullOrWhiteSpace(s) && SegmentRegex().IsMatch(s);

    private static bool IsSafeBranch(string s) =>
        !string.IsNullOrWhiteSpace(s) && BranchRegex().IsMatch(s);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._/\-]+$")]
    private static partial Regex BranchRegex();
}
