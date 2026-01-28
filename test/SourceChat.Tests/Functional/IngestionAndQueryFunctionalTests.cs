using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Ingest;
using SourceChat.Features.Query;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.AI;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.FileSystem;
using SourceChat.Infrastructure.Storage;
using IngestionResult = SourceChat.Features.Shared.IngestionResult;

namespace SourceChat.Tests.Functional;

public class IngestionAndQueryFunctionalTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    private readonly Dictionary<string, string?> _originalEnvVars;

    public IngestionAndQueryFunctionalTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"SourceChatFunctionalTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        _testDbPath = Path.Combine(Path.GetTempPath(), $"SourceChatFunctionalTest_{Guid.NewGuid()}.db");

        // Save original environment variables
        _originalEnvVars = new Dictionary<string, string?>
        {
            ["AI_PROVIDER"] = Environment.GetEnvironmentVariable("AI_PROVIDER"),
            ["OLLAMA_ENDPOINT"] = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT"),
            ["OLLAMA_EMBEDDING_MODEL"] = Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL"),
            ["OLLAMA_CHAT_MODEL"] = Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL"),
            ["SQLITE_DB_PATH"] = Environment.GetEnvironmentVariable("SQLITE_DB_PATH")
        };

        // Set up test environment variables for Ollama
        Environment.SetEnvironmentVariable("AI_PROVIDER", "Ollama");
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", "http://localhost:11434");
        Environment.SetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL", "all-minilm");
        Environment.SetEnvironmentVariable("OLLAMA_CHAT_MODEL", "llama3.2");
        Environment.SetEnvironmentVariable("SQLITE_DB_PATH", _testDbPath);
    }

    [Fact]
    public async Task IngestAndQuery_WithTestMarkdownFile_ShouldReturnRelevantResults()
    {
        // Arrange: Create test markdown file with known content
        string testMarkdownContent = """
            # Functional Test Document

            This is a test document for SourceChat functional testing.

            ## Key Information

            SourceChat is a .NET console application designed for ingesting and querying code documentation using RAG (Retrieval-Augmented Generation).

            ## Features

            - Markdown parsing
            - Vector embedding
            - Semantic search capabilities
            - Query functionality

            ## Test Keywords

            This document contains specific test keywords: FUNCTIONAL_TEST_KEYWORD_12345

            ## Additional Details

            The application uses Microsoft.Extensions.DataIngestion for processing documents and Microsoft.SemanticKernel.Connectors.SqliteVec for vector storage.
            """;

        string testFile = Path.Combine(_testDirectory, "test_functional.md");
        await File.WriteAllTextAsync(testFile, testMarkdownContent);

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning); // Reduce noise in test output
        });

        ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());
        EmbeddingGeneratorFactory embeddingFactory = new(config, loggerFactory.CreateLogger<EmbeddingGeneratorFactory>());
        ChatClientFactory chatClientFactory = new(config, loggerFactory.CreateLogger<ChatClientFactory>());
        VectorStoreProvider vectorStoreProvider = new(embeddingFactory, config, loggerFactory.CreateLogger<VectorStoreProvider>());
        FileChangeDetector changeDetector = new(config);
        IngestionDocumentReader reader = new MarkdownReader();
        IngestionService ingestionService = new(config, vectorStoreProvider, embeddingFactory, chatClientFactory, changeDetector, loggerFactory, reader);
        QueryService queryService = new(chatClientFactory, config, vectorStoreProvider, loggerFactory.CreateLogger<QueryService>());

        // Act - Ingestion
        Result<IngestionResult> ingestionResult = await ingestionService.IngestDirectoryAsync(
            _testDirectory,
            "*.md",
            ChunkingStrategy.Section,
            incremental: false);

        // Assert - Ingestion succeeded
        Assert.True(ingestionResult.IsSuccess);
        Assert.NotNull(ingestionResult.Value);
        Assert.True(ingestionResult.Value.FilesProcessed > 0, "At least one file should be processed");
        Assert.Equal(0, ingestionResult.Value.Errors);

        // Act - Query with question that should match the test content
        Result<string> queryResponse = await queryService.QueryAsync("What is SourceChat?", maxResults: 5);

        // Assert - Query returned relevant results
        Assert.True(queryResponse.IsSuccess);
        Assert.NotNull(queryResponse.Value);
        Assert.NotEmpty(queryResponse.Value);
        Assert.DoesNotContain("No data has been ingested yet", queryResponse.Value);
        Assert.DoesNotContain("I couldn't find any relevant information", queryResponse.Value);

        // Verify the response contains expected keywords from the test file
        // The response should mention .NET, RAG, or similar concepts from the test document
        Assert.True(
            queryResponse.Value.Contains(".NET", StringComparison.OrdinalIgnoreCase) ||
            queryResponse.Value.Contains("RAG", StringComparison.OrdinalIgnoreCase) ||
            queryResponse.Value.Contains("Retrieval", StringComparison.OrdinalIgnoreCase) ||
            queryResponse.Value.Contains("SourceChat", StringComparison.OrdinalIgnoreCase),
            $"Query result should contain relevant information. Result: {queryResponse.Value}");

        Console.WriteLine($"Ingestion - Files Processed: {ingestionResult.Value.FilesProcessed}, Errors: {ingestionResult.Value.Errors}");
        Console.WriteLine($"Query Result: {queryResponse.Value}");
    }

    [Fact]
    public async Task Query_WithoutIngestion_ShouldThrowException()
    {
        // Arrange: Set up QueryService without ingesting any data
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());
        ChatClientFactory chatClientFactory = new(config, loggerFactory.CreateLogger<ChatClientFactory>());
        EmbeddingGeneratorFactory embeddingFactory = new(config, loggerFactory.CreateLogger<EmbeddingGeneratorFactory>());
        VectorStoreProvider vectorStoreProvider = new(embeddingFactory, config, loggerFactory.CreateLogger<VectorStoreProvider>());
        QueryService queryService = new(chatClientFactory, config, vectorStoreProvider, loggerFactory.CreateLogger<QueryService>());

        // Act & Assert: Query without any ingestion should return a failure result
        Result<string> queryResponse = await queryService.QueryAsync("What is SourceChat?", maxResults: 5);
        Assert.True(queryResponse.IsFailure);
        Assert.Equal(QueryErrors.CollectionNotFound().Message, queryResponse.Error.Message);

        Console.WriteLine("Query correctly returned failure when no data was ingested");
    }

    public void Dispose()
    {
        // Restore original environment variables
        foreach (KeyValuePair<string, string?> kvp in _originalEnvVars)
        {
            if (kvp.Value == null)
            {
                Environment.SetEnvironmentVariable(kvp.Key, null);
            }
            else
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }

        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            try
            {
                File.Delete(_testDbPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // Clean up tracking file
        string trackingFile = Path.ChangeExtension(_testDbPath, ".tracking.json");
        if (File.Exists(trackingFile))
        {
            try
            {
                File.Delete(trackingFile);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}