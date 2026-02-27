using System.Text.Json;
using DirForge.Models;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    private static async Task HandleToolsListAsync(HttpContext context, JsonElement? id, DirForgeOptions options)
    {
        var tools = new List<object>
        {
            new
            {
                name = "list_directory",
                description = "List contents of a directory. Returns file and folder names with sizes and modification dates.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "get_file_info",
                description = "Get detailed metadata about a file including size, modification date, and MIME type.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path to the file." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "read_file",
                description = "Read the text contents of a file. Only works for text files within the size limit. Binary files (images, videos, executables, etc.) will be rejected.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path to the file." },
                        ["maxBytes"] = new { type = "integer", description = "Maximum bytes to read (default: server limit)." },
                        ["startLine"] = new { type = "integer", description = "If provided, returns only lines starting from this 1-based line number." },
                        ["endLine"] = new { type = "integer", description = "If provided, returns only lines up to and including this 1-based line number." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "get_file_hashes",
                description = "Compute CRC32, MD5, SHA1, SHA256, and SHA512 hashes of a file.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path to the file." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "verify_sidecar",
                description = "Verify a file's integrity against a sidecar checksum file. Looks for a sidecar file in the same directory as the target file by appending a hash extension (e.g. for 'movie.mkv' it checks 'movie.mkv.sha256', 'movie.mkv.md5', 'movie.mkv.sfv', etc.). Supported sidecar formats: .sha512, .sha256, .sha1, .md5 (and their *sum variants), and .sfv (CRC32). Returns the algorithm used, the expected hash from the sidecar, the freshly computed hash, and whether they match.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path to the file to verify (the data file, not the sidecar)." }
                    },
                    required = new[] { "path" }
                }
            },
            new
            {
                name = "get_directory_tree",
                description = "Get a visual tree representation of a directory structure, similar to the 'tree' command.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." },
                        ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 3, max: 10)." },
                        ["includeSizes"] = new { type = "boolean", description = "Include file/directory sizes in output (default: false)." }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "group_tree_by_type",
                description = "Summarize all files under a directory grouped by file extension, with counts and total sizes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." },
                        ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 10)." }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "group_tree_by_date",
                description = "Summarize all files under a directory grouped by modification date buckets (Today, Yesterday, This week, This month, This year, Older).",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." }
                    },
                    required = Array.Empty<string>()
                }
            }
        };

        tools.Add(new
        {
            name = "find_largest",
            description = "Find the largest files under a directory, sorted by size descending.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." },
                    ["maxResults"] = new { type = "integer", description = "Maximum results to return (default: 20, max: 100)." },
                    ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 10, max: 10)." },
                    ["include"] = new { type = "string", description = "Comma-separated glob patterns to include (e.g. '*.log, *.txt'). Only matching files are returned. Supports * and ? wildcards. Empty means all files." },
                    ["exclude"] = new { type = "string", description = "Comma-separated glob patterns to exclude (e.g. 'node_modules, *.tmp'). Matching files and directories are skipped. Supports * and ? wildcards." }
                },
                required = Array.Empty<string>()
            }
        });

        tools.Add(new
        {
            name = "find_recent",
            description = "Find the most recently modified files under a directory, sorted by date descending.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." },
                    ["maxResults"] = new { type = "integer", description = "Maximum results to return (default: 20, max: 100)." },
                    ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 10, max: 10)." },
                    ["include"] = new { type = "string", description = "Comma-separated glob patterns to include (e.g. '*.log, *.txt'). Only matching files are returned. Supports * and ? wildcards. Empty means all files." },
                    ["exclude"] = new { type = "string", description = "Comma-separated glob patterns to exclude (e.g. 'node_modules, *.tmp'). Matching files and directories are skipped. Supports * and ? wildcards." }
                },
                required = Array.Empty<string>()
            }
        });

        tools.Add(new
        {
            name = "compare_directories",
            description = "Compare two directories side by side. Shows files only in the first, only in the second, files that differ, and identical files.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["path1"] = new { type = "string", description = "Relative path to the first directory." },
                    ["path2"] = new { type = "string", description = "Relative path to the second directory." },
                    ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 10, max: 10)." },
                    ["include"] = new { type = "string", description = "Comma-separated glob patterns to include (e.g. '*.log, *.txt'). Only matching files are returned. Supports * and ? wildcards. Empty means all files." },
                    ["exclude"] = new { type = "string", description = "Comma-separated glob patterns to exclude (e.g. 'node_modules, *.tmp'). Matching files and directories are skipped. Supports * and ? wildcards." }
                },
                required = new[] { "path1", "path2" }
            }
        });

        tools.Add(new
        {
            name = "find_potential_duplicates",
            description = "Find potential duplicate files by grouping files with identical sizes then hashing candidates. Uses XXH3-128 with 5 × 2 MiB probabilistic chunk sampling (first, last, and 3 pseudo-random interior chunks per file) — results are potential duplicates, not guaranteed exact matches.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." },
                    ["minSize"] = new { type = "integer", description = "Minimum file size in bytes to consider (default: 1)." },
                    ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 10, max: 10)." },
                    ["maxResults"] = new { type = "integer", description = "Maximum number of duplicate groups to return (default: 20, max: 100)." },
                    ["include"] = new { type = "string", description = "Comma-separated glob patterns to include (e.g. '*.log, *.txt'). Only matching files are returned. Supports * and ? wildcards. Empty means all files." },
                    ["exclude"] = new { type = "string", description = "Comma-separated glob patterns to exclude (e.g. 'node_modules, *.tmp'). Matching files and directories are skipped. Supports * and ? wildcards." }
                },
                required = Array.Empty<string>()
            }
        });

        if (options.EnableSearch)
        {
            tools.Add(new
            {
                name = "search",
                description = "Search for files and folders by name pattern.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["query"] = new { type = "string", description = "Search query (filename pattern)." },
                        ["path"] = new { type = "string", description = "Directory to search within. Empty for root." },
                        ["maxResults"] = new { type = "integer", description = "Maximum results to return (default: 200)." }
                    },
                    required = new[] { "query" }
                }
            });

            if (options.AllowFileDownload)
            {
                tools.Add(new
                {
                    name = "search_content",
                    description = "Search for text inside files (grep-like). Returns matching lines with line numbers. Only searches text files.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["query"] = new { type = "string", description = "Case-insensitive text to search for inside files." },
                            ["path"] = new { type = "string", description = "Directory to search within. Empty for root." },
                            ["maxResults"] = new { type = "integer", description = "Maximum matching lines to return (default: 50, max: 200)." },
                            ["maxFileSize"] = new { type = "integer", description = "Skip files larger than this many bytes (default: 2 MB, max: 10 MB)." }
                        },
                        required = new[] { "query" }
                    }
                });
            }
        }

        tools.Add(new
        {
            name = "diff_snapshots",
            description = "Detect filesystem changes in a directory. On first call (without a previousToken), returns a snapshot token. On subsequent calls, pass the previous token to get a structured diff of added, removed, and modified files. The token is self-contained — no server-side state is stored.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["path"] = new { type = "string", description = "Relative path within the file server root. Empty string for root." },
                    ["previousToken"] = new { type = "string", description = "Snapshot token from a previous call. Omit for first snapshot." },
                    ["maxDepth"] = new { type = "integer", description = "Maximum depth to traverse (default: 10, max: 10)." }
                },
                required = Array.Empty<string>()
            }
        });

        if (options.OpenArchivesInline)
        {
            tools.Add(new
            {
                name = "read_archive_entry",
                description = "Read the text contents of a file entry inside an archive (ZIP, TAR, TAR.GZ).",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["archivePath"] = new { type = "string", description = "Relative path to the archive file." },
                        ["entryPath"] = new { type = "string", description = "Path of the entry within the archive." },
                        ["maxBytes"] = new { type = "integer", description = "Maximum bytes to read from the entry (default: server limit)." }
                    },
                    required = new[] { "archivePath", "entryPath" }
                }
            });

            tools.Add(new
            {
                name = "list_archive_entries",
                description = "List files and directories inside an archive (ZIP, TAR, TAR.GZ). Supports browsing into subdirectories within the archive.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["archivePath"] = new { type = "string", description = "Relative path to the archive file." },
                        ["path"] = new { type = "string", description = "Directory path within the archive. Empty string for root." }
                    },
                    required = new[] { "archivePath" }
                }
            });
        }

        await WriteJsonRpcResultAsync(context, id, new { tools });
    }

    private static async Task HandleToolsCallAsync(HttpContext context, JsonElement? id, JsonElement? @params, DirForgeOptions options)
    {
        var toolName = @params?.TryGetProperty("name", out var nameProp) == true ? nameProp.GetString() : null;
        var arguments = @params?.TryGetProperty("arguments", out var argsProp) == true ? argsProp : (JsonElement?)null;

        if (string.IsNullOrWhiteSpace(toolName))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Missing tool 'name'");
            return;
        }

        switch (toolName)
        {
            case "list_directory":
                await HandleToolListDirectory(context, id, arguments, options);
                break;
            case "get_file_info":
                await HandleToolGetFileInfo(context, id, arguments, options);
                break;
            case "read_file":
                await HandleToolReadFile(context, id, arguments, options);
                break;
            case "search":
                await HandleToolSearch(context, id, arguments, options);
                break;
            case "get_file_hashes":
                await HandleToolGetFileHashes(context, id, arguments, options);
                break;
            case "verify_sidecar":
                await HandleToolVerifySidecar(context, id, arguments, options);
                break;
            case "get_directory_tree":
                await HandleToolGetDirectoryTree(context, id, arguments, options);
                break;
            case "group_tree_by_type":
                await HandleToolGroupTreeByType(context, id, arguments, options);
                break;
            case "group_tree_by_date":
                await HandleToolGroupTreeByDate(context, id, arguments, options);
                break;
            case "read_archive_entry":
                await HandleToolReadArchiveEntry(context, id, arguments, options);
                break;
            case "list_archive_entries":
                await HandleToolListArchiveEntries(context, id, arguments, options);
                break;
            case "search_content":
                await HandleToolSearchContent(context, id, arguments, options);
                break;
            case "find_largest":
                await HandleToolFindLargest(context, id, arguments, options);
                break;
            case "find_recent":
                await HandleToolFindRecent(context, id, arguments, options);
                break;
            case "compare_directories":
                await HandleToolCompareDirectories(context, id, arguments, options);
                break;
            case "find_potential_duplicates":
                await HandleToolFindPotentialDuplicates(context, id, arguments, options);
                break;
            case "diff_snapshots":
                await HandleToolDiffSnapshots(context, id, arguments, options);
                break;
            default:
                await WriteJsonRpcErrorAsync(context, id, -32602, $"Unknown tool: {toolName}");
                break;
        }
    }
}
