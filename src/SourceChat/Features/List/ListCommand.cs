using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.FileSystem;

namespace SourceChat.Features.List;

internal static class ListCommand
{
    public static Command Create(Option<LogLevel> logLevelOption)
    {
        Option<bool> statsOption = new(name: "--stats")
        {
            Description = "Show detailed statistics",
            DefaultValueFactory = result => false
        };

        Command command = new(name: "list", description: "List ingested files and statistics");

        command.Add(statsOption);
        command.Add(logLevelOption);

        command.SetAction(async result =>
        {
            LogLevel logLevel = result.GetValue(logLevelOption);

            ServiceCollection collection = ServiceCollectionFactory.Create(logLevel);
            await using ServiceProvider serviceProvider = collection.BuildServiceProvider();

            ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ListCommand));
            ConfigurationService config = serviceProvider.GetRequiredService<ConfigurationService>();

            bool showStats = result.GetRequiredValue(statsOption);

            try
            {
                FileChangeDetector changeDetector = serviceProvider.GetRequiredService<FileChangeDetector>();
                List<string> trackedFiles = changeDetector.GetTrackedFiles();

                if (trackedFiles.Count == 0)
                {
                    Console.WriteLine("No files have been ingested yet.");

                    return;
                }

                Console.WriteLine($"Tracked Files ({trackedFiles.Count}):");
                Console.WriteLine();

                foreach (string file in trackedFiles.OrderBy(f => f))
                {
                    Console.WriteLine($"  {file}");
                }

                if (!showStats)
                {
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Database Statistics:");

                FileInfo dbFile = new(config.SqliteDbPath);
                if (dbFile.Exists)
                {
                    Console.WriteLine($"  Database size: {dbFile.Length / 1024.0:F2} KB");
                    Console.WriteLine($"  Last modified: {dbFile.LastWriteTime}");
                }

                // TODO: Add vector store statistics (chunk count, etc.)
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                logger.LogError(ex, "List command failed");
            }
        });

        return command;
    }
}