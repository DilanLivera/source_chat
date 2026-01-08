using System.CommandLine;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Storage;

namespace SourceChat.Features.Ingest;

internal static class IngestCommand
{
    public static Command Create(Option<LogLevel> logLevelOption)
    {
        Argument<string> pathArgument = new(name: "path")
        {
            Description = "Path to the directory containing files to ingest"
        };

        Option<ChunkingStrategy> strategyOption = new(name: "--strategy")
        {
            Description = "Chunking strategy to use (Semantic, Section, Structure)",
            DefaultValueFactory = result => ChunkingStrategy.Semantic
        };

        Option<string> patternsOption = new(name: "--patterns")
        {
            Description = "File patterns to include (semicolon-separated)",
            DefaultValueFactory = result => "*.cs;*.md;*.txt;*.json;*.yml;*.yaml;*.xml"
        };

        Option<bool> incrementalOption = new(name: "--incremental")
        {
            Description = "Only process changed files",
            DefaultValueFactory = result => true
        };

        Command command = new(name: "ingest",
                              description: "Ingest code and documentation files into the vector database");

        command.Add(pathArgument);

        command.Add(strategyOption);
        command.Add(patternsOption);
        command.Add(incrementalOption);
        command.Add(logLevelOption);

        command.SetAction(result =>
        {
            LogLevel logLevel = result.GetValue(logLevelOption);
            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(logLevel);
            });

            ILogger logger = loggerFactory.CreateLogger(categoryName: nameof(IngestCommand));
            ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());

            string path = result.GetRequiredValue(pathArgument);
            ChunkingStrategy strategy = result.GetRequiredValue(strategyOption);
            string patterns = result.GetRequiredValue(patternsOption);
            bool incremental = result.GetRequiredValue(incrementalOption);

            try
            {
                config.Validate();

                if (!Directory.Exists(path))
                {
                    Console.WriteLine($"Error: Directory not found: {path}");

                    return;
                }

                Console.WriteLine($"Ingesting files from: {path}");
                Console.WriteLine($"Strategy: {strategy}");
                Console.WriteLine($"Patterns: {patterns}");
                Console.WriteLine($"Incremental: {incremental}");
                Console.WriteLine();

                VectorStoreManager vectorStoreManager = new(config,
                                                            loggerFactory.CreateLogger<VectorStoreManager>());
                FileChangeDetector changeDetector = new(config.SqliteDbPath);
                IngestionService ingestionService = new(config,
                                                        vectorStoreManager,
                                                        changeDetector,
                                                        loggerFactory.CreateLogger<IngestionService>());

                IngestionResult ingestionResult = ingestionService.IngestDirectoryAsync(path,
                                                                                        patterns,
                                                                                        strategy,
                                                                                        incremental)
                                                                  .GetAwaiter()
                                                                  .GetResult();

                if (ingestionResult.Errors > 0)
                {
                    Console.WriteLine($"\n⚠ Completed with {ingestionResult.Errors} error(s)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                logger.LogError(ex, "Ingestion failed");
            }
        });

        return command;
    }
}