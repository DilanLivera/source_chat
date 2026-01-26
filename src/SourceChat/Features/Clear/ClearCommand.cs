using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Storage;

namespace SourceChat.Features.Clear;

internal static class ClearCommand
{
    public static Command Create(Option<LogLevel> logLevelOption)
    {
        Option<bool> confirmOption = new(name: "--confirm")
        {
            Description = "Skip confirmation prompt",
            DefaultValueFactory = result => false
        };

        Command command = new(name: "clear", description: "Clear all ingested data");

        command.Add(confirmOption);
        command.Add(logLevelOption);

        command.SetAction(async result =>
        {
            LogLevel logLevel = result.GetValue(logLevelOption);

            ServiceCollection collection = ServiceCollectionFactory.Create(logLevel);
            await using ServiceProvider serviceProvider = collection.BuildServiceProvider();

            ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ClearCommand));
            ConfigurationService config = serviceProvider.GetRequiredService<ConfigurationService>();

            bool confirm = result.GetRequiredValue(confirmOption);

            try
            {
                if (!confirm)
                {
                    Console.Write("Are you sure you want to clear all ingested data? (yes/no): ");
                    string? response = Console.ReadLine()?
                                              .Trim()
                                              .ToLowerInvariant();

                    if (response != "yes" && response != "y")
                    {
                        Console.WriteLine("Operation cancelled.");

                        return;
                    }
                }

                FileChangeDetector changeDetector = serviceProvider.GetRequiredService<FileChangeDetector>();
                changeDetector.ClearTracking();

                if (File.Exists(config.SqliteDbPath))
                {
                    File.Delete(config.SqliteDbPath);
                    Console.WriteLine($"Deleted database: {config.SqliteDbPath}");
                }

                Console.WriteLine("All data cleared successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                logger.LogError(ex, "Clear command failed");
            }
        });

        return command;
    }
}