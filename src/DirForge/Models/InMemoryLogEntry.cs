using Microsoft.Extensions.Logging;

namespace DirForge.Models;

public sealed record InMemoryLogEntry(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);
