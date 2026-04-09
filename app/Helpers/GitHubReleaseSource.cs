using System.Reflection;

namespace GHelper.Helpers
{
    internal static class GitHubReleaseSource
    {
        private const string DefaultRepository = "youtonghy/g-helper";
        private const string MetadataKey = "GitHubRepository";

        private static readonly Lazy<string> RepositoryLazy = new(ResolveRepository);

        public static string Repository => RepositoryLazy.Value;
        public static string ReleasesPageUrl => $"https://github.com/{Repository}/releases";
        public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{Repository}/releases/latest";

        public static string GetReleaseDownloadUrl(string tag, string assetName)
        {
            return $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";
        }

        public static bool IsReleaseAssetUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return false;

            var expectedPrefix = "/" + Repository + "/releases/download/";
            return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveRepository()
        {
            var repository = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == MetadataKey)
                ?.Value;

            if (!string.IsNullOrWhiteSpace(repository))
            {
                Logger.WriteLine($"GitHub release repository: {repository}");
                return repository;
            }

            Logger.WriteLine($"GitHub release repository metadata is missing, fallback to {DefaultRepository}");
            return DefaultRepository;
        }
    }
}
