using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Octokit;

namespace DependencyFlow.Pages
{
    public class IncomingModel : PageModel
    {
        private static readonly Regex _repoParser = new Regex(@"https?://(www\.)?github.com/(?<owner>[A-Za-z0-9-_\.]+)/(?<repo>[A-Za-z0-9-_\.]+)");
        private readonly swaggerClient _client;
        private readonly GitHubClient _github;
        private readonly ILogger<IncomingModel> _logger;

        public IncomingModel(swaggerClient client, GitHubClient github, SlaOptions slaOptions, ILogger<IncomingModel> logger)
        {
            _client = client;
            _github = github;
            SlaOptions = slaOptions;
            _logger = logger;
        }

        public SlaOptions SlaOptions { get; }

        public IReadOnlyList<IncomingRepo>? IncomingRepositories { get; private set; }
        public RateLimit? CurrentRateLimit { get; private set; }

        public Build? Build { get; private set; }

        public string? ChannelName { get; private set; }

        public async Task OnGet(int channelId, string owner, string repo)
        {
            var channel = await _client.GetChannelAsync(channelId, ApiVersion13._20190116);
            ChannelName = channel.Name;

            var repoUrl = $"https://github.com/{owner}/{repo}";
            var latest = await _client.GetLatestAsync(repoUrl, null, null, channelId, null, null, false, ApiVersion10._20190116, CancellationToken.None);
            var graph = await _client.GetBuildGraphAsync(latest.Id, (ApiVersion9)ApiVersion40._20190116, CancellationToken.None);
            Build = graph.Builds[latest.Id.ToString()];

            var incoming = new List<IncomingRepo>();
            foreach (var dep in Build.Dependencies)
            {
                var lastConsumedBuildOfDependency = graph.Builds[dep.BuildId.ToString()];

                var gitHubInfo = GetGitHubInfo(lastConsumedBuildOfDependency);

                if (!IncludeRepo(gitHubInfo))
                {
                    continue;
                }

                var (commitDistance, commitAge) = await GetCommitInfo(gitHubInfo, lastConsumedBuildOfDependency);

                var oldestPublishedButUnconsumedBuild = await GetOldestUnconsumedBuild(lastConsumedBuildOfDependency.Id);

                incoming.Add(new IncomingRepo(
                    lastConsumedBuildOfDependency,
                    shortName: gitHubInfo?.Repo ?? "",
                    oldestPublishedButUnconsumedBuild,
                    GetCommitUrl(lastConsumedBuildOfDependency),
                    GetBuildUrl(lastConsumedBuildOfDependency),
                    commitDistance,
                    commitAge));
            }
            IncomingRepositories = incoming;

            CurrentRateLimit = _github.GetLastApiInfo().RateLimit;
        }

        private async Task<Build?> GetOldestUnconsumedBuild(int lastConsumedBuildOfDependencyId)
        {
            // Note: We fetch `build` again here so that it will have channel information, which it doesn't when coming from the graph :(
            var build = await _client.GetBuildAsync(lastConsumedBuildOfDependencyId, ApiVersion8._20190116, CancellationToken.None);
            var buildCollection = await _client.ListBuildsAsync(
                build.GitHubRepository,
                commit: null,
                buildNumber: null,
                channelId: build.Channels.FirstOrDefault(c => c.Classification == "product" || c.Classification == "tools")?.Id,
                notBefore: build.DateProduced.Subtract(TimeSpan.FromSeconds(5)),
                notAfter: null,
                loadCollections: false,
                page: null,
                perPage: null,
                api_version: ApiVersion6._20190116,
                CancellationToken.None);
            var publishedBuildsOfDependency = new List<Build>(buildCollection);

            var last = publishedBuildsOfDependency.LastOrDefault();
            if (last == null)
            {
                this._logger.LogWarning("Last build didn't match last consumed build, treating dependency '{Dependency}' as up to date", build.GitHubRepository);
                return null;
            }

            if (last.AzureDevOpsBuildId != build.AzureDevOpsBuildId)
            {
                this._logger.LogWarning("Last build didn't match last consumed build");
            }

            return publishedBuildsOfDependency.Count > 1
                ? publishedBuildsOfDependency[publishedBuildsOfDependency.Count - 2]
                : null;
        }

        public GitHubInfo? GetGitHubInfo(Build? build)
        {
            GitHubInfo? gitHubInfo = null;
            if (!string.IsNullOrEmpty(build?.GitHubRepository))
            {
                var match = _repoParser.Match(build.GitHubRepository);
                if (match.Success)
                {
                    gitHubInfo = new GitHubInfo(
                        match.Groups["owner"].Value,
                        match.Groups["repo"].Value);
                }
            }

            return gitHubInfo;
        }

        public string GetBuildUrl(Build? build)
            => build == null
                ? "(unknown)"
                : $"https://dev.azure.com/{build.AzureDevOpsAccount}/{build.AzureDevOpsProject}/_build/results?buildId={build.AzureDevOpsBuildId}&view=results";

        private bool IncludeRepo(GitHubInfo? gitHubInfo)
        {
            if (string.Equals(gitHubInfo?.Owner, "dotnet", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(gitHubInfo?.Repo, "blazor", StringComparison.OrdinalIgnoreCase))
            {
                // We don't want to track dependency staleness of the Blazor repo
                // because it's not part of our process of automated dependency PRs.
                return false;
            }

            return true;
        }

        public string GetCommitUrl(Build? build)
        {
            return build switch
            {
                null => "unknown",
                _ => string.IsNullOrEmpty(build.GitHubRepository)
                       ? $"{build.AzureDevOpsRepository}/commits?itemPath=%2F&itemVersion=GC{build.Commit}"
                       : $"{build.GitHubRepository}/commits/{build.Commit}",
            };
        }

        public string GetDateProduced(Build? build)
        {
            return build switch
            {
                null => "unknown",
                _ => build.DateProduced.Humanize()
            };
        }

        private async Task<CompareResult?> GetCommitsBehindAsync(GitHubInfo gitHubInfo, Build build)
        {
            try
            {
                var comparison = await _github.Repository.Commit.Compare(gitHubInfo.Owner, gitHubInfo.Repo, build.Commit, build.GitHubBranch);

                return comparison;
            }
            catch (NotFoundException)
            {
                _logger.LogWarning("Failed to compare commit history for '{0}/{1}' between '{2}' and '{3}'.", gitHubInfo.Owner, gitHubInfo.Repo,
                    build.Commit, build.GitHubBranch);
                return null;
            }
        }

        private async Task<(int? commitDistance, DateTimeOffset? commitAge)> GetCommitInfo(GitHubInfo? gitHubInfo, Build lastConsumedBuild)
        {
            DateTimeOffset? commitAge = null;
            int? commitDistance = null;
            if (gitHubInfo != null)
            {
                var comparison = await GetCommitsBehindAsync(gitHubInfo, lastConsumedBuild);

                // We're using the branch as the "head" so "ahead by" is actually how far the branch (i.e. "master") is
                // ahead of the commit. So it's also how far **behind** the commit is from the branch head.
                commitDistance = comparison?.AheadBy;

                if (comparison != null && comparison.Commits.Count > 0)
                {
                    // Follow the first parent starting at the last unconsumed commit until we find the commit directly after our current consumed commit
                    var nextCommit = comparison.Commits[comparison.Commits.Count - 1];
                    while (nextCommit.Parents[0].Sha != lastConsumedBuild.Commit)
                    {
                        bool foundCommit = false;
                        foreach (var commit in comparison.Commits)
                        {
                            if (commit.Sha == nextCommit.Parents[0].Sha)
                            {
                                nextCommit = commit;
                                foundCommit = true;
                                break;
                            }
                        }

                        if (foundCommit == false)
                        {
                            // Happens if there are over 250 commits
                            // We would need to use a paging API to follow commit history over 250 commits
                            _logger.LogDebug("Failed to follow commit parents and find correct commit age. Falling back to the date the build was produced");
                            return (commitDistance, null);
                        }
                    }

                    commitAge = nextCommit.Commit.Committer.Date;
                }
            }
            return (commitDistance, commitAge);
        }
    }

    public class IncomingRepo
    {
        public Build LastConsumedBuild { get; }
        public string ShortName { get; }
        public int? CommitDistance { get; }
        public DateTimeOffset? CommitAge { get; }
        public string CommitUrl { get; }
        public string BuildUrl { get; }
        public Build? OldestPublishedButUnconsumedBuild { get; }

        public IncomingRepo(Build lastConsumedBuild, string shortName, Build? oldestPublishedButUnconsumedBuild, string commitUrl, string buildUrl, int? commitDistance, DateTimeOffset? commitAge)
        {
            LastConsumedBuild = lastConsumedBuild;
            ShortName = shortName;
            OldestPublishedButUnconsumedBuild = oldestPublishedButUnconsumedBuild;
            CommitUrl = commitUrl;
            BuildUrl = buildUrl;
            CommitDistance = commitDistance;
            CommitAge = commitAge;
        }
    }

    public class GitHubInfo
    {
        public string Owner { get; }
        public string Repo { get; }

        public GitHubInfo(string owner, string repo)
        {
            Owner = owner;
            Repo = repo;
        }
    }
}
