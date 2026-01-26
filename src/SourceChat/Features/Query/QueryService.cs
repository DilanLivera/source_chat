using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OllamaSharp;
using OpenAI;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Storage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace SourceChat.Features.Query;

internal class QueryService
{
    private readonly ConfigurationService _config;
    private readonly VectorStoreManager _vectorStoreManager;
    private readonly ILogger<QueryService> _logger;
    private IChatClient? _chatClient;

    public QueryService(
        ConfigurationService config,
        VectorStoreManager vectorStoreManager,
        ILogger<QueryService> logger)
    {
        _config = config;
        _vectorStoreManager = vectorStoreManager;
        _logger = logger;
    }

    public async Task<string> QueryAsync(
        string question,
        int maxResults = 5,
        ConversationContext? context = null)
    {
        try
        {
            SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

            // Use GetDynamicCollection since GetCollection doesn't support Dictionary<string, object?>
            // The collection was created by VectorStoreWriter with dynamic schema
            VectorStoreCollection<object, Dictionary<string, object?>> collection;
            try
            {
                // GetDynamicCollection requires a definition - we need to provide the vector dimensions
                int embeddingDimension = GetEmbeddingDimension();
                VectorStoreCollectionDefinition definition = new()
                {
                    // The definition needs to match how VectorStoreWriter created the collection
                    // Since it's dynamic, we just need the vector dimensions
                };
                collection = vectorStore.GetDynamicCollection(name: "data", definition);
            }
            catch (VectorStoreException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
            {
                return "No data has been ingested yet. Please run the 'ingest' command first.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collection 'data'");
                throw new InvalidOperationException("Unable to access the 'data' collection. Please ensure files have been ingested first.", ex);
            }

            List<VectorSearchResult<Dictionary<string, object?>>> searchResults = await collection.SearchAsync(question, top: maxResults)
                                                                                   .ToListAsync();

            if (searchResults.Count == 0)
            {
                return "I couldn't find any relevant information in the codebase to answer your question.";
            }

            // Prepare context from search results
            List<string> retrievedChunks = searchResults
                                           .Select(r =>
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
            string systemPrompt = BuildSystemPrompt(contextText);

            IChatClient chatClient = GetChatClient();

            List<ChatMessage> messages = [new(ChatRole.System, systemPrompt)];

            // Add conversation history if available
            if (context is not null)
            {
                messages.AddRange(context.History);
                context.AddRetrievedChunks(retrievedChunks);
            }

            // Add current question
            messages.Add(new ChatMessage(ChatRole.User, question));

            // Get response
            ChatResponse response = await chatClient.GetResponseAsync(messages);
            string answer = response.Text;

            // Update context
            if (context != null)
            {
                context.AddUserMessage(question);
                context.AddAssistantMessage(answer);
            }

            return answer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during query execution");

            throw;
        }
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

            try
            {
                Console.Write("\nSourceChat: ");
                string answer = await QueryAsync(input, maxResults: 5, context: context);
                Console.WriteLine(answer);
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}\n");
                _logger.LogError(ex, "Error in interactive query");
            }
        }
    }

    private IChatClient GetChatClient()
    {
        if (_chatClient != null)
        {
            return _chatClient;
        }

        string provider = _config.AiProvider.ToLowerInvariant();

        _chatClient = provider switch
        {
            "openai" => CreateOpenAIChatClient(),
            "azureopenai" => CreateAzureOpenAIChatClient(),
            "ollama" => CreateOllamaChatClient(),
            _ => throw new InvalidOperationException($"Unknown AI provider: {_config.AiProvider}")
        };

        _logger.LogInformation("Initialized chat client for provider: {Provider}", _config.AiProvider);

        return _chatClient;
    }

    private IChatClient CreateOpenAIChatClient()
    {
        OpenAIClient client = new(_config.OpenAiApiKey);

        return client.GetChatClient(_config.OpenAiChatModel).AsIChatClient();
    }

    private IChatClient CreateAzureOpenAIChatClient()
    {
        ChatCompletionsClient client = new(
                                           new Uri(_config.AzureOpenAiEndpoint),
                                           new Azure.AzureKeyCredential(_config.AzureOpenAiApiKey));

        return client.AsIChatClient();
    }

    private IChatClient CreateOllamaChatClient() => new OllamaApiClient(new Uri(_config.OllamaEndpoint),
                                                                        _config.OllamaChatModel);

    private int GetEmbeddingDimension()
    {
        string provider = _config.AiProvider.ToLowerInvariant();
        string embeddingModel = provider switch
        {
            "openai" => _config.OpenAiEmbeddingModel,
            "azureopenai" => _config.AzureOpenAiEmbeddingDeployment,
            "ollama" => _config.OllamaEmbeddingModel,
            _ => "text-embedding-3-small"
        };

        // Return dimension based on model
        return embeddingModel.ToLowerInvariant() switch
        {
            "text-embedding-3-small" => 1536,
            "text-embedding-3-large" => 3072,
            "text-embedding-ada-002" => 1536,
            "all-minilm" => 384, // Ollama's all-minilm model
            "qwen3-embedding" => 4096, // Qwen3 embedding model has 4096 dimensions
            _ => 1536 // Default fallback
        };
    }

    private string BuildSystemPrompt(string context)
    {
        return $"""
                You are SourceChat, an AI assistant that helps developers understand their codebase.

                You have access to the following relevant code snippets and documentation from the codebase:

                {context}

                Instructions:
                - Answer questions based on the provided code context
                - Be specific and reference actual code when possible
                - If you're unsure or the context doesn't contain relevant information, say so
                - Provide file paths and line references when available
                - Explain technical concepts clearly
                - For follow-up questions, consider the conversation history

                Answer the user's question clearly and concisely.
                """;
    }
}