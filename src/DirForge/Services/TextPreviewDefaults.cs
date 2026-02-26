namespace DirForge.Services;

public static class TextPreviewDefaults
{
    public const int MaxBytes = 131072;

    public static readonly HashSet<string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/yaml",
        "application/toml", "application/sql", "application/graphql",
        "application/x-sh", "application/javascript", "application/x-httpd-php",
        "application/x-tex", "application/x-latex", "application/hcl",
        "application/x-subrip", "application/x-tcl", "application/x-msdos-program"
    };

    public static bool IsTextLikeMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (mimeType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
            mimeType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return MimeTypes.Contains(mimeType);
    }
}
