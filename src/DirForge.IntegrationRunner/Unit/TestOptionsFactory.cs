using DirForge.Models;

namespace DirForge.IntegrationRunner.Unit;

internal static class TestOptionsFactory
{
    public static DirForgeOptions Create(string rootPath)
    {
        return new DirForgeOptions
        {
            RootPath = rootPath,
            Port = 8080,
            ListenIp = "127.0.0.1",
            BasicAuthUser = null,
            BasicAuthPass = null,
            DefaultTheme = "dark",
            CalculateDirectorySizes = false,
            AllowFolderDownload = true,
            AllowFileDownload = true,
            OpenArchivesInline = true,
            EnableSearch = true,
            SearchMaxDepth = 4,
            OperationTimeBudgetMs = 5000,
            EnableSharing = true,
            HideDotfiles = false,
            HidePathPatterns = [],
            DenyDownloadExtensions = [],
            MaxZipSize = 0,
            SiteTitle = "DirForge",
            MaxPreviewFileSize = 10 * 1024 * 1024,
            MaxFileSizeForHashing = 50 * 1024 * 1024,
            ShareSecret = "test-share-secret-12345",
            ShareSecretWarning = null,
            ForwardedHeadersEnabled = false,
            ForwardedHeadersForwardLimit = 1,
            ForwardedHeadersKnownProxies = [],
            ExternalAuthEnabled = false,
            ExternalAuthIdentityHeader = "X-Auth-User",
            EnableDefaultRateLimiter = false,
            RateLimitPerIp = 100,
            RateLimitGlobal = 1000,
            DashboardEnabled = false,
            EnableMetricsEndpoint = false,
            DashboardAuthUser = null,
            DashboardAuthPass = null,
            EnableWebDav = false,
            EnableS3Endpoint = false,
            S3BucketName = "dirforge",
            S3Region = "us-east-1",
            S3AccessKeyId = null,
            S3SecretAccessKey = null,
            EnableJsonApi = false,
            EnableMcpEndpoint = false,
            McpReadFileSizeLimit = 1024 * 1024,
            BearerToken = null,
            BearerTokenHeaderName = "Authorization"
        };
    }
}
