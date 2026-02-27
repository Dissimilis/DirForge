namespace DirForge.Models;

public sealed class DirForgeOptions
{
    public string RootPath { get; set; } = null!;
    public int Port { get; set; }
    public string ListenIp { get; set; } = null!;
    public string? BasicAuthUser { get; set; }
    public string? BasicAuthPass { get; set; }
    public string DefaultTheme { get; set; } = null!;
    public bool CalculateDirectorySizes { get; set; }
    public bool AllowFolderDownload { get; set; }
    public bool AllowFileDownload { get; set; }
    public bool OpenArchivesInline { get; set; }
    public bool EnableSearch { get; set; }
    public int SearchMaxDepth { get; set; }
    public int OperationTimeBudgetMs { get; set; }
    public bool EnableSharing { get; set; }
    public bool HideDotfiles { get; set; }
    public string[] HidePathPatterns { get; set; } = null!;
    public string[] DenyDownloadExtensions { get; set; } = null!;
    public long MaxZipSize { get; set; }
    public string? SiteTitle { get; set; }
    public long MaxPreviewFileSize { get; set; }
    public long MaxFileSizeForHashing { get; set; }
    public string ShareSecret { get; set; } = null!;
    public string? ShareSecretWarning { get; set; }
    public bool ForwardedHeadersEnabled { get; set; }
    public int ForwardedHeadersForwardLimit { get; set; }
    public string[] ForwardedHeadersKnownProxies { get; set; } = null!;
    public bool ExternalAuthEnabled { get; set; }
    public string ExternalAuthIdentityHeader { get; set; } = null!;
    public bool EnableDefaultRateLimiter { get; set; }
    public int RateLimitPerIp { get; set; }
    public int RateLimitGlobal { get; set; }
    public bool DashboardEnabled { get; set; }
    public bool EnableMetricsEndpoint { get; set; }
    public string? DashboardAuthUser { get; set; }
    public string? DashboardAuthPass { get; set; }
    public bool EnableWebDav { get; set; } = true;
    public bool EnableS3Endpoint { get; set; }
    public string S3BucketName { get; set; } = null!;
    public string S3Region { get; set; } = null!;
    public string? S3AccessKeyId { get; set; }
    public string? S3SecretAccessKey { get; set; }
    public bool EnableJsonApi { get; set; } = true;
    public bool EnableMcpEndpoint { get; set; } = true;
    public long McpReadFileSizeLimit { get; set; } = 1_048_576;
    public string? BearerToken { get; set; }
    public string BearerTokenHeaderName { get; set; } = null!;
    public int ListingCacheTtlSeconds { get; set; }
    public bool BearerTokenEnabled => !string.IsNullOrWhiteSpace(BearerToken);
    public bool AuthEnabled => !string.IsNullOrWhiteSpace(BasicAuthUser) && !string.IsNullOrWhiteSpace(BasicAuthPass);
    public bool DashboardAuthEnabled => !string.IsNullOrWhiteSpace(DashboardAuthUser) && !string.IsNullOrWhiteSpace(DashboardAuthPass);
    public string ResolvedS3AccessKeyId => !string.IsNullOrWhiteSpace(S3AccessKeyId) ? S3AccessKeyId : BasicAuthUser ?? string.Empty;
    public string ResolvedS3SecretAccessKey => !string.IsNullOrWhiteSpace(S3SecretAccessKey) ? S3SecretAccessKey : BasicAuthPass ?? string.Empty;
}
