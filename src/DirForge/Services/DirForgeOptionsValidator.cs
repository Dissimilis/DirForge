using System.Net;
using DirForge.Models;
using Microsoft.Extensions.Options;

namespace DirForge.Services;

public sealed class DirForgeOptionsValidator : IValidateOptions<DirForgeOptions>
{
    public ValidateOptionsResult Validate(string? name, DirForgeOptions options)
    {
        var failures = new List<string>();

        if (options.Port is < 1 or > 65535)
        {
            failures.Add("Port must be between 1 and 65535.");
        }

        if (options.RateLimitPerIp < 1)
        {
            failures.Add("RateLimitPerIp must be at least 1.");
        }

        if (options.RateLimitGlobal < 1)
        {
            failures.Add("RateLimitGlobal must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(options.ListenIp))
        {
            failures.Add("ListenIp is required.");
        }
        else if (!IPAddress.TryParse(options.ListenIp, out _))
        {
            failures.Add("ListenIp must be a valid IPv4 or IPv6 address.");
        }

        if (options.SearchMaxDepth < 0)
        {
            failures.Add("SearchMaxDepth must be 0 or greater.");
        }

        if (options.OperationTimeBudgetMs != 0 && options.OperationTimeBudgetMs < 100)
        {
            failures.Add("OperationTimeBudgetMs must be 0 (disabled) or at least 100.");
        }

        if (options.MaxZipSize < 0)
        {
            failures.Add("MaxZipSize must be 0 or greater.");
        }

        if (options.MaxPreviewFileSize < 0)
        {
            failures.Add("MaxPreviewFileSize must be 0 or greater.");
        }

        if (options.MaxFileSizeForHashing < 0)
        {
            failures.Add("MaxFileSizeForHashing must be 0 or greater.");
        }

        if (options.ForwardedHeadersForwardLimit < 1)
        {
            failures.Add("ForwardedHeadersForwardLimit must be at least 1.");
        }

        if (!string.Equals(options.DefaultTheme, "dark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.DefaultTheme, "light", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("DefaultTheme must be either 'dark' or 'light'.");
        }

        if (string.IsNullOrWhiteSpace(options.ExternalAuthIdentityHeader))
        {
            failures.Add("ExternalAuthIdentityHeader cannot be empty.");
        }

        if (options.HidePathPatterns is null)
        {
            failures.Add("HidePathPatterns is required.");
        }

        if (options.DenyDownloadExtensions is null)
        {
            failures.Add("DenyDownloadExtensions is required.");
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            failures.Add("RootPath is required.");
        }
        else
        {
            if (!Directory.Exists(options.RootPath))
            {
                failures.Add(
                    $"RootPath '{options.RootPath}' does not exist.");
            }
            else if (!DirectoryReadinessHelper.IsDirectoryReadable(options.RootPath))
            {
                failures.Add($"RootPath '{options.RootPath}' is not readable.");
            }
        }

        if (options.ForwardedHeadersKnownProxies is null)
        {
            failures.Add("ForwardedHeadersKnownProxies is required.");
        }
        else
        {
            foreach (var knownProxy in options.ForwardedHeadersKnownProxies)
            {
                if (!IPAddress.TryParse(knownProxy, out _))
                {
                    failures.Add($"ForwardedHeadersKnownProxies contains an invalid IP address '{knownProxy}'.");
                }
            }
        }

        if (options.ListingCacheTtlSeconds is < 1 or > 2_592_000)
        {
            failures.Add("ListingCacheTtlSeconds must be between 1 and 2592000 (30 days).");
        }

        if (options.BearerTokenEnabled && string.IsNullOrWhiteSpace(options.BearerTokenHeaderName))
        {
            failures.Add("BearerTokenHeaderName cannot be empty when BearerToken is set.");
        }

        if (options.EnableS3Endpoint)
        {
            if (string.IsNullOrWhiteSpace(options.ResolvedS3AccessKeyId))
            {
                failures.Add("S3 endpoint is enabled but no access key is configured. Set S3AccessKeyId or BasicAuthUser.");
            }

            if (string.IsNullOrWhiteSpace(options.ResolvedS3SecretAccessKey))
            {
                failures.Add("S3 endpoint is enabled but no secret key is configured. Set S3SecretAccessKey or BasicAuthPass.");
            }

            if (string.IsNullOrWhiteSpace(options.S3BucketName))
            {
                failures.Add("S3BucketName cannot be empty when S3 endpoint is enabled.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
