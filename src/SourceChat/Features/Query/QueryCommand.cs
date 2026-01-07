using System.CommandLine;
using Microsoft.Extensions.Logging;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Storage;

namespace SourceChat.Features.Query;

internal static class QueryCommand
{
    public static Command Create(ILoggerFactory loggerFactory)
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

        command.SetAction(result =>
        {
            ILogger logger = loggerFactory.CreateLogger(categoryName: nameof(QueryCommand));
            ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());

            string? question = result.GetRequiredValue(questionArgument);
            int maxResults = result.GetRequiredValue(maxResultsOption);
            bool interactive = result.GetRequiredValue(interactiveOption);

            try
            {
                config.Validate();

                VectorStoreManager vectorStoreManager = new(config,
                                                            loggerFactory.CreateLogger<VectorStoreManager>());
                QueryService queryService = new(config,
                                                vectorStoreManager,
                                                loggerFactory.CreateLogger<QueryService>());

                if (string.IsNullOrWhiteSpace(question) || interactive)
                {
                    queryService.RunInteractiveQueryAsync()
                                .GetAwaiter()
                                .GetResult();
                }
                else
                {
                    Console.WriteLine($"Question: {question}\n");
                    string answer = queryService.QueryAsync(question, maxResults)
                                                .GetAwaiter()
                                                .GetResult();
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