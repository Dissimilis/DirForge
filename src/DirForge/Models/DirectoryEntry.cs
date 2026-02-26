namespace DirForge.Models;

public sealed class DirectoryEntry
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string Extension { get; init; }
    public required string Type { get; init; }
    public required string IconPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; set; }
    public required string HumanSize { get; set; }
    public required string SizeTooltip { get; set; }
    public required DateTime Modified { get; init; }
    public required string ModifiedString { get; init; }
}
