using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Shared;
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

            config.Validate();

            QueryService queryService = serviceProvider.GetRequiredService<QueryService>();

            if (string.IsNullOrWhiteSpace(question) || interactive)
            {
                await queryService.RunInteractiveQueryAsync();
            }
            else
            {
                Console.WriteLine($"Question: {question}\n");
                Result<string> queryResult = await queryService.QueryAsync(question, maxResults);

                if (queryResult.IsFailure)
                {
                    logger.LogError("Query failed: {Error}", queryResult.Error);
                    Console.WriteLine($"\nError: {queryResult.Error.Message}");
                    Environment.ExitCode = 1;
                    return;
                }

                Console.WriteLine($"Answer: {queryResult.Value}");
            }
        });

        return command;
    }
}