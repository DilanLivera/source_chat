using System.CommandLine;
using DotNetEnv;
using Microsoft.Extensions.Logging;
using SourceChat;

Env.Load();

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

ILogger<Program> logger = loggerFactory.CreateLogger<Program>();

try
{
    RootCommand rootCommand = new(description: "SourceChat - AI-Powered Code Documentation Assistant");

    rootCommand.Subcommands.Add(IngestCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(QueryCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(ListCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(ClearCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(ConfigCommand.Create(loggerFactory));

    return await rootCommand.Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    logger.LogError(ex, "Fatal error occurred");

    return 1;
}

namespace SourceChat
{
    public enum ChunkingStrategy
    {
        Semantic,
        Section,
        Structure
    }

    public class DocumentMetadata
    {
        public required string FilePath { get; init; }
        public required string FileType { get; init; }
        public DateTime LastModified { get; init; }
        public long FileSize { get; init; }
        public Dictionary<string, string> CustomMetadata { get; init; } = new();
    }

    public class CodeMetadata
    {
        public string? Namespace { get; set; }
        public string? ClassName { get; set; }
        public List<string> Methods { get; set; } = [];
        public List<string> Properties { get; set; } = [];
        public string? XmlSummary { get; set; }
    }

    public class FileTrackingInfo
    {
        public DateTime LastModified { get; set; }
        public string Hash { get; set; } = string.Empty;
        public DateTime LastProcessed { get; set; }
    }

    public class IngestionResult
    {
        public int FilesProcessed { get; set; }
        public int TotalChunks { get; set; }
        public int Errors { get; set; }
    }
}