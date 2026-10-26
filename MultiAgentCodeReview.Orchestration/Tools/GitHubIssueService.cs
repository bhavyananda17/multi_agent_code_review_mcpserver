using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Tools;

public interface IGitHubIssueService
{
    /// <summary>
    /// Resolves an optional issue reference (full issue URL, or a number such as "123" / "#123")
    /// into the issue's title and body. Returns null when <paramref name="issueRef"/> is blank,
    /// which keeps the commit-only flow untouched. Throws with an actionable message when an
    /// explicitly requested issue cannot be fetched — failing before any LLM call is made.
    /// </summary>
    Task<IssueContext?> GetIssueAsync(string? issueRef, string repositoryPath, CancellationToken cancellationToken = default);
}

public sealed record GitHubIssueRef(string? Owner, string? Repo, int Number);

public class GitHubIssueService : IGitHubIssueService
{
    // Protects the TPM budget: the issue text is injected into the triage prompt.
    private const int MaxBodyChars = 4000;
    private const string ApiBase = "https://api.github.com";

    private static readonly Regex NamePart = new(@"^(?!\.{1,2}$)[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex ScpRemote = new(
        @"^[\w.-]+@github\.com:(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly HttpClient _http;
    private readonly string _token;

    public GitHubIssueService(HttpClient http, IOptions<PipelineConfig> config)
    {
        _http = http;
        _token = config.Value.GitHub.Token;
    }

    public async Task<IssueContext?> GetIssueAsync(string? issueRef, string repositoryPath, CancellationToken cancellationToken = default)
    {
        var parsed = ParseIssueRef(issueRef);
        if (parsed == null)
            return null;

        var owner = parsed.Owner;
        var repo = parsed.Repo;
        if (owner == null || repo == null)
        {
            // Bare number: resolve owner/repo from the checkout's origin remote. This also works
            // for temp clones made by RepositorySourceResolver, whose origin is the clone URL.
            var remote = await GetOriginUrlAsync(repositoryPath, cancellationToken);
            (owner, repo) = ParseGitHubRemote(remote)
                ?? throw new InvalidOperationException(
                    "Cannot resolve issue number: the repository's 'origin' remote is not a github.com repository. " +
                    "Pass the full issue URL instead.");
        }

        return await FetchAsync(owner, repo, parsed.Number, cancellationToken);
    }

    /// <summary>
    /// Accepts "https://github.com/{owner}/{repo}/issues/{n}" (trailing slash / query / fragment
    /// ignored), "{n}" or "#{n}". Returns null for blank input; throws on anything else.
    /// </summary>
    public static GitHubIssueRef? ParseIssueRef(string? issueRef)
    {
        if (string.IsNullOrWhiteSpace(issueRef))
            return null;

        var value = issueRef.Trim();

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
            {
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 4 &&
                    segments[2].Equals("issues", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(segments[3], out var number) && number > 0 &&
                    NamePart.IsMatch(segments[0]) && NamePart.IsMatch(segments[1]))
                {
                    return new GitHubIssueRef(segments[0], segments[1], number);
                }
            }

            throw new ArgumentException(
                $"'{value}' is not a GitHub issue URL. Expected https://github.com/<owner>/<repo>/issues/<number>.");
        }

        if (int.TryParse(value.TrimStart('#'), out var bare) && bare > 0)
            return new GitHubIssueRef(null, null, bare);

        throw new ArgumentException(
            $"'{value}' is not a valid issue reference. Use an issue URL, or a number such as 123 or #123.");
    }

    /// <summary>Extracts (owner, repo) from a github.com remote URL, or null if it is anything else.</summary>
    public static (string Owner, string Repo)? ParseGitHubRemote(string remoteUrl)
    {
        var remote = remoteUrl.Trim();

        var scp = ScpRemote.Match(remote);
        if (scp.Success)
            return ValidName(scp.Groups["owner"].Value, scp.Groups["repo"].Value);

        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? segments[1][..^4]
                    : segments[1];
                return ValidName(segments[0], repo);
            }
        }

        return null;
    }

    private static (string, string)? ValidName(string owner, string repo) =>
        NamePart.IsMatch(owner) && NamePart.IsMatch(repo) ? (owner, repo) : null;

    private static async Task<string> GetOriginUrlAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("remote");
        psi.ArgumentList.Add("get-url");
        psi.ArgumentList.Add("origin");

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = (await stdoutTask).Trim();
        await stderrTask;

        if (process.ExitCode != 0 || stdout.Length == 0)
        {
            throw new InvalidOperationException(
                "Cannot resolve issue number: the repository has no 'origin' remote. Pass the full issue URL instead.");
        }

        // Deliberately never echoed in error messages: an origin URL can embed credentials.
        return stdout;
    }

    private async Task<IssueContext> FetchAsync(string owner, string repo, int number, CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/repos/{owner}/{repo}/issues/{number}";
        var label = $"{owner}/{repo}#{number}";
        var hasToken = !string.IsNullOrWhiteSpace(_token);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("MultiAgentCodeReview"); // GitHub rejects requests without a User-Agent
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (hasToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach api.github.com to fetch issue {label}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Timed out fetching issue {label} from api.github.com.", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.NotFound:
                    throw new InvalidOperationException(hasToken
                        ? $"Issue {label} was not found, or the configured GitHub token cannot read it."
                        : $"Issue {label} was not found. If the repository is private, set MULTIAGENT_GITHUB_TOKEN " +
                          "(or GITHUB_TOKEN) to a token with read access to its issues.");
                case HttpStatusCode.Gone:
                    throw new InvalidOperationException($"Issue {label} no longer exists (HTTP 410).");
                case HttpStatusCode.Unauthorized:
                    throw new InvalidOperationException("GitHub rejected the configured token (HTTP 401). Check MULTIAGENT_GITHUB_TOKEN / GITHUB_TOKEN.");
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.TooManyRequests:
                    throw new InvalidOperationException(DescribeRateLimit(response, hasToken, label));
                default:
                    throw new InvalidOperationException($"GitHub API returned HTTP {(int)response.StatusCode} for issue {label}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var labels = new List<string>();
            if (root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in l.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                        labels.Add(name);
                }
            }

            return new IssueContext(owner, repo, number, title.Trim(), CleanBody(body), htmlUrl, labels);
        }
    }

    // Issue templates are full of <!-- instructions to the author --> comments; they are pure noise
    // for triage and eat into the prompt budget.
    private static string CleanBody(string body)
    {
        var cleaned = HtmlComment.Replace(body, "").Trim();
        return cleaned.Length > MaxBodyChars
            ? cleaned[..MaxBodyChars] + "\n... [issue body truncated]"
            : cleaned;
    }

    private static string DescribeRateLimit(HttpResponseMessage response, bool hasToken, string label)
    {
        var exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0";

        if (!exhausted && response.StatusCode == HttpStatusCode.Forbidden)
            return $"GitHub denied access to issue {label} (HTTP 403).";

        var reset = "";
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            long.TryParse(resetValues.FirstOrDefault(), out var epoch))
        {
            reset = $" It resets at {DateTimeOffset.FromUnixTimeSeconds(epoch):u}.";
        }

        return hasToken
            ? $"GitHub API rate limit exceeded while fetching issue {label}.{reset}"
            : $"GitHub API rate limit exceeded (unauthenticated: 60 requests/hour) while fetching issue {label}.{reset} " +
              "Set MULTIAGENT_GITHUB_TOKEN (or GITHUB_TOKEN) to raise the limit.";
    }
}
