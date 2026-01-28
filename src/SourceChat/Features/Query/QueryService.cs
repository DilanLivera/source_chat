using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.AI;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Storage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace SourceChat.Features.Query;

internal sealed class QueryService
{
    private readonly ConfigurationService _config;
    private readonly IChatClient _chatClient;
    private readonly ILogger<QueryService> _logger;
    private readonly SqliteVectorStore _vectorStore;

    public QueryService(
        ChatClientFactory chatClientFactory,
        ConfigurationService config,
        VectorStoreProvider vectorStoreProvider,
        ILogger<QueryService> logger)
    {
        _config = config;
        _vectorStore = vectorStoreProvider.GetVectorStore();
        _logger = logger;
        _chatClient = chatClientFactory.Create();
    }

    public async Task<Result<string>> QueryAsync(
        string question,
        int maxResults = 5,
        ConversationContext? context = null)
    {
        // Use GetDynamicCollection since GetCollection doesn't support Dictionary<string, object?>
        // The collection was created by VectorStoreWriter with dynamic schema
        VectorStoreCollection<object, Dictionary<string, object?>> collection;
        try
        {
            // GetDynamicCollection requires a definition with key property, vector property, and data properties
            int embeddingDimension = _config.GetEmbeddingDimension();
            // Create a definition that matches the schema created by VectorStoreWriter<string>
            // VectorStoreWriter creates collections with Dictionary<string, object?> records
            // The definition needs to specify the key property, vector property, and data properties
            // Based on the actual database schema, the key column is "key" and there are additional columns
            VectorStoreCollectionDefinition definition = new()
            {
                Properties =
                                                             [
                                                                 // Key property - VectorStoreWriter<string> uses "key" as the column name
                                                                 // Key properties must be one of: int, long, string, Guid
                                                                 new VectorStoreKeyProperty(name: "key", typeof(string)),
                                                                 // Vector property - stores the embedding vector (stored in vec_data virtual table as "embedding")
                                                                 new VectorStoreVectorProperty(name: "embedding", typeof(ReadOnlyMemory<float>), dimensions: embeddingDimension),
                                                                 // Data properties - match the actual schema columns
                                                                 new VectorStoreDataProperty(name: "content", typeof(string)),
                                                                 new VectorStoreDataProperty(name: "context", typeof(string)),
                                                                 new VectorStoreDataProperty(name: "documentid", typeof(string))
                                                             ]
            };
            collection = _vectorStore.GetDynamicCollection(name: "data", definition);
        }
        catch (VectorStoreException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
        {
            _logger.LogError(ex, "Collection 'data' not found");
            return Result<string>.Failure(QueryErrors.CollectionNotFound());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection 'data'");
            return Result<string>.Failure(QueryErrors.CollectionAccessError(ex.Message));
        }

        List<VectorSearchResult<Dictionary<string, object?>>> searchResults;
        try
        {
            searchResults = await collection.SearchAsync(question, top: maxResults)
                                            .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during vector search");
            return Result<string>.Failure(QueryErrors.QueryExecutionError(ex.Message));
        }

        if (searchResults.Count == 0)
        {
            return Result<string>.Failure(QueryErrors.NoSearchResults());
        }

        // Prepare context from search results
        List<string> retrievedChunks = searchResults.Select(r =>
                                                    {
                                                        // Extract content from dictionary record
                                                        string text = "";
                                                        if (r.Record.TryGetValue("content", out object? contentObj) && contentObj is string content)
                                                        {
                                                            text = content;
                                                        }
                                                        return $"[Score: {r.Score:F4}] {text}";
                                                    })
                                                    .ToList();

        // Build prompt with context
        string contextText = string.Join("\n\n", retrievedChunks);
        string systemPrompt = $"""
                               You are SourceChat, an AI assistant that helps developers understand their codebase.

                               You have access to the following relevant code snippets and documentation from the codebase:

                               {contextText}

                               Instructions:
                               - Answer questions based on the provided code context
                               - Be specific and reference actual code when possible
                               - If you're unsure or the context doesn't contain relevant information, say so
                               - Provide file paths and line references when available
                               - Explain technical concepts clearly
                               - For follow-up questions, consider the conversation history

                               Answer the user's question clearly and concisely.
                               """;

        List<ChatMessage> messages = [new(ChatRole.System, systemPrompt)];

        // Add conversation history if available
        if (context is not null)
        {
            messages.AddRange(context.History);
            context.AddRetrievedChunks(retrievedChunks);
        }

        // Add current question
        messages.Add(new ChatMessage(ChatRole.User, question));

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(messages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during chat response generation");
            return Result<string>.Failure(QueryErrors.QueryExecutionError(ex.Message));
        }

        string answer = response.Text;

        if (context is null)
        {
            return Result<string>.Success(answer);
        }

        context.AddUserMessage(question);
        context.AddAssistantMessage(answer);

        return Result<string>.Success(answer);
    }

    public async Task RunInteractiveQueryAsync()
    {
        Console.WriteLine("\n=== SourceChat Interactive Mode ===");
        Console.WriteLine("Ask questions about your codebase. Type 'exit' to quit, 'clear' to reset conversation.\n");

        ConversationContext context = new();

        while (true)
        {
            Console.Write("You: ");
            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Goodbye!");

                break;
            }

            if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                context.Clear();
                Console.WriteLine("Conversation history cleared.\n");

                continue;
            }

            Console.Write("\nSourceChat: ");
            Result<string> queryResult = await QueryAsync(input, maxResults: 5, context: context);

            if (queryResult.IsFailure)
            {
                Console.WriteLine($"Error: {queryResult.Error.Message}\n");
                _logger.LogError("Query failed: {Error}", queryResult.Error);
            }
            else
            {
                Console.WriteLine(queryResult.Value);
                Console.WriteLine();
            }
        }
    }
}