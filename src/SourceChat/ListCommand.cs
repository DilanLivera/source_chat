using System.CommandLine;
using Microsoft.Extensions.Logging;

namespace SourceChat;

internal static class ListCommand
{
    public static Command Create(ILoggerFactory loggerFactory)
    {
        Command command = new(name: "list", description: "List ingested files and statistics");

        Option<bool> statsOption = new(name: "--stats")
        {
            Description = "Show detailed statistics",
            DefaultValueFactory = result => false
        };

        command.Add(statsOption);

        command.SetAction(result =>
                           {
                               ILogger logger = loggerFactory.CreateLogger(categoryName: "ListCommand");
                               ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());

                               bool showStats = result.GetRequiredValue(statsOption);

                               try
                               {
                                   FileChangeDetector changeDetector = new(config.SqliteDbPath);
                                   List<string> trackedFiles = changeDetector.GetTrackedFiles();

                                   if (!trackedFiles.Any())
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

                                   if (showStats)
                                   {
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