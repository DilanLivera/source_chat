using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Infrastructure.Configuration;

namespace SourceChat.Features.Query;

internal static class QueryCommand
{
    public static Command Create(Option<LogLevel> logLevelOption)
    {
        Argument<string?> questionArgument = new(name: "question")
        {
            Description = "Question to ask (if not provided, enters interactive mode)",
            DefaultValueFactory = result => null
        };

        Option<int> maxResultsOption = new(name: "--max-results")
        {
            Description = "Maximum number of results to retrieve",
            DefaultValueFactory = result => 5
        };

        Option<bool> interactiveOption = new(name: "--interactive")
        {
            Description = "Start interactive conversation mode",
            DefaultValueFactory = result => false
        };

        Command command = new(name: "query", description: "Query the ingested codebase");

        command.Add(questionArgument);
        command.Add(maxResultsOption);
        command.Add(interactiveOption);
        command.Add(logLevelOption);

        command.SetAction(async result =>
        {
            LogLevel logLevel = result.GetValue(logLevelOption);

            ServiceCollection collection = ServiceCollectionFactory.Create(logLevel);
            await using ServiceProvider serviceProvider = collection.BuildServiceProvider();

            ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(QueryCommand));
            ConfigurationService config = serviceProvider.GetRequiredService<ConfigurationService>();

            string? question = result.GetRequiredValue(questionArgument);
            int maxResults = result.GetRequiredValue(maxResultsOption);
            bool interactive = result.GetRequiredValue(interactiveOption);

            try
            {
                config.Validate();

                QueryService queryService = serviceProvider.GetRequiredService<QueryService>();

                if (string.IsNullOrWhiteSpace(question) || interactive)
                {
                    await queryService.RunInteractiveQueryAsync();
                }
                else
                {
                    Console.WriteLine($"Question: {question}\n");
                    string answer = await queryService.QueryAsync(question, maxResults);
                    Console.WriteLine($"Answer: {answer}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                logger.LogError(ex, "Query failed");
            }
        });

        return command;
    }
}