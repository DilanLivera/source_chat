using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OllamaSharp;
using OpenAI;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Parsing;
using SourceChat.Infrastructure.Storage;
using IngestionResult = SourceChat.Features.Shared.IngestionResult;

namespace SourceChat.Features.Ingest;

// https://learn.microsoft.com/en-us/dotnet/ai/conceptual/data-ingestion
internal class IngestionService
{
    private readonly ConfigurationService _config;
    private readonly VectorStoreManager _vectorStoreManager;
    private readonly FileChangeDetector _changeDetector;
    private readonly ILogger<IngestionService> _logger;
    private readonly List<IFileParser> _parsers;
    private VectorStoreCollection<object, Dictionary<string, object?>>? _lastIngestionCollection;

    public IngestionService(
        ConfigurationService config,
        VectorStoreManager vectorStoreManager,
        FileChangeDetector changeDetector,
        ILogger<IngestionService> logger)
    {
        _config = config;
        _vectorStoreManager = vectorStoreManager;
        _changeDetector = changeDetector;
        _logger = logger;

        _parsers =
        [
            new CSharpParser(),
            new MarkdownParser(),
            new JsonParser(),
            new YamlParser(),
            new XmlParser(),
            new PlainTextParser()
        ];
    }

    public async Task<IngestionResult> IngestDirectoryAsync(string path,
                                                            string patterns,
                                                            ChunkingStrategy strategy,
                                                            bool incremental)
    {
        IngestionDocumentReader reader = new MarkdownReader();
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        // Use the vector store manager to get the embedding generator based on configured provider
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = _vectorStoreManager.GetEmbeddingGenerator();

        // Get chat client based on provider (may fail if Ollama isn't running, so we'll handle that)
        IChatClient? chatClient = null;
        try
        {
            chatClient = GetChatClient(loggerFactory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get chat client. Enrichers will be disabled. Error: {Message}", ex.Message);
        }

        // Only create enrichers if we have a chat client
        IngestionDocumentProcessor? imageAlternativeTextEnricher = null;
        IngestionChunkProcessor<string>? summaryEnricher = null;

        if (chatClient != null)
        {
            EnricherOptions enricherOptions = new(chatClient)
            {
                LoggerFactory = loggerFactory
            };
            imageAlternativeTextEnricher = new ImageAlternativeTextEnricher(enricherOptions);
            summaryEnricher = new SummaryEnricher(enricherOptions);
        }
        else
        {
            _logger.LogInformation("Skipping enrichers (ImageAlternativeTextEnricher, SummaryEnricher) due to missing chat client");
        }

        // Determine tokenizer model based on provider
        string tokenizerModel = GetTokenizerModel();
        IngestionChunkerOptions chunkerOptions = new(TiktokenTokenizer.CreateForModel(tokenizerModel))
        {
            MaxTokensPerChunk = _config.MaxTokensPerChunk,
            OverlapTokens = _config.ChunkOverlapTokens,
        };

        IngestionChunker<string> chunker = CreateChunker(strategy, chunkerOptions, embeddingGenerator);

        // Use the vector store from the manager (it already has the correct connection string and embedding generator)
        SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

        // Determine embedding dimension based on provider and model
        int embeddingDimension = GetEmbeddingDimension();

        using VectorStoreWriter<string> writer = new(vectorStore,
                                                     dimensionCount: embeddingDimension,
                                                     new VectorStoreWriterOptions
                                                     {
                                                         CollectionName = "data"
                                                     });

        // Compose data ingestion pipeline
        using IngestionPipeline<string> pipeline = new(reader,
                                                       chunker,
                                                       writer,
                                                       new IngestionPipelineOptions
                                                       {
                                                           ActivitySourceName = "SourceChat",
                                                       },
                                                       loggerFactory);

        // Only add enrichers if they were successfully created
        if (imageAlternativeTextEnricher != null)
        {
            pipeline.DocumentProcessors.Add(imageAlternativeTextEnricher);
        }
        if (summaryEnricher != null)
        {
            pipeline.ChunkProcessors.Add(summaryEnricher);
        }

        DirectoryInfo directory = new(path);

        // Use the provided patterns parameter instead of hardcoding
        int filesProcessed = 0;
        int errors = 0;

        try
        {
            _logger.LogInformation("Starting to process files from directory: {Path} with patterns: {Patterns}", path, patterns);

            // Check if directory exists and has files
            if (!Directory.Exists(path))
            {
                _logger.LogError("Directory does not exist: {Path}", path);
                throw new DirectoryNotFoundException($"Directory not found: {path}");
            }

            // List files that match the patterns for debugging
            string[] patternArray = patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string> matchingFiles = new();
            foreach (string pattern in patternArray)
            {
                matchingFiles.AddRange(Directory.GetFiles(path, pattern, SearchOption.AllDirectories));
            }
            _logger.LogInformation("Found {Count} files matching patterns: {Files}", matchingFiles.Count, string.Join(", ", matchingFiles.Select(Path.GetFileName)));

            int resultCount = 0;

            // Process each pattern separately since ProcessAsync only accepts a single search pattern
            foreach (string pattern in patternArray)
            {
                IAsyncEnumerable<Microsoft.Extensions.DataIngestion.IngestionResult>? results = null;
                try
                {
                    results = pipeline.ProcessAsync(directory, searchPattern: pattern);
                    _logger.LogInformation("Pipeline.ProcessAsync called successfully for pattern: {Pattern}", pattern);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start pipeline processing for pattern {Pattern}: {Message}", pattern, ex.Message);
                    throw;
                }

                await foreach (Microsoft.Extensions.DataIngestion.IngestionResult result in results)
                {
                    resultCount++;
                    _logger.LogInformation("Completed processing '{DocumentId}'. Succeeded: '{Succeeded}'.", result.DocumentId, result.Succeeded);

                    // Track the file in FileChangeDetector regardless of success/failure
                    // DocumentId might be a URI (file://) or a file path
                    string filePath = result.DocumentId;

                    // Handle URI format (file:///path/to/file)
                    if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        Uri uri = new(filePath);
                        filePath = uri.LocalPath;
                    }

                    // Ensure we have a full path (DocumentId might be relative)
                    if (!Path.IsPathRooted(filePath))
                    {
                        filePath = Path.Combine(path, filePath);
                    }
                    filePath = Path.GetFullPath(filePath);

                    // Try to track the file regardless of success/failure, as long as it exists
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            FileInfo fileInfo = new(filePath);
                            string hash = await _changeDetector.GetFileHashAsync(filePath);
                            _changeDetector.UpdateFileTracking(filePath, fileInfo.LastWriteTime, hash);
                            _logger.LogInformation("Tracked file: {FilePath} (Succeeded: {Succeeded})", filePath, result.Succeeded);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to track file: {FilePath}", filePath);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("File not found for tracking: {FilePath} (DocumentId: {DocumentId})", filePath, result.DocumentId);
                    }

                    if (result.Succeeded)
                    {
                        filesProcessed++;
                        // Note: Chunk count would need to be tracked differently as the pipeline doesn't expose it directly
                    }
                    else
                    {
                        errors++;
                        _logger.LogWarning("Failed to process document: {DocumentId}", result.DocumentId);
                    }
                }
            }

            _logger.LogInformation("Finished processing. Results received: {ResultCount}, Files processed: {FilesProcessed}, Errors: {Errors}", resultCount, filesProcessed, errors);

            if (resultCount == 0)
            {
                _logger.LogWarning("No results received from pipeline. This might indicate that no files were processed or the pipeline failed silently.");
            }
            else
            {
                // Store the collection reference for later use in GetIngestionSummaryAsync
                // Only store it after processing is complete and data has been written
                try
                {
                    _lastIngestionCollection = writer.VectorStoreCollection;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get vector store collection reference: {Message}", ex.Message);
                }
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed with exception: {Message}", ex.Message);
            throw;
        }
        finally
        {
            // Save tracking information to persist file tracking
            try
            {
                _changeDetector.SaveTracking();
                _logger.LogInformation("File tracking saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save file tracking");
            }
        }

        IngestionResult ingestionResult = new()
        {
            FilesProcessed = filesProcessed,
            Errors = errors
        };
        // TotalChunks would need additional tracking to be accurate

        return ingestionResult;
    }

    public async Task<List<SummaryChunk>> GetIngestionSummaryAsync(int topResults = 5)
    {
        List<SummaryChunk> summaryChunks = new();

        try
        {
            VectorStoreCollection<object, Dictionary<string, object?>>? collection = _lastIngestionCollection;

            // If we don't have a collection reference from the last ingestion, try to get it from the vector store
            if (collection == null)
            {
                SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

                // Use GetDynamicCollection since GetCollection doesn't support Dictionary<string, object?>
                // VectorStoreWriter<string> creates collections with Dictionary<string, object?> records
                try
                {
                    int embeddingDimension = GetEmbeddingDimension();
                    VectorStoreCollectionDefinition definition = CreateCollectionDefinition(embeddingDimension);
                    collection = vectorStore.GetDynamicCollection(name: "data", definition);
                }
                catch (VectorStoreException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
                {
                    _logger.LogInformation("Collection 'data' does not exist yet. No summary available.");
                    return summaryChunks;
                }
                catch (VectorStoreException)
                {
                    _logger.LogInformation("Vector store collection not available.");
                    return summaryChunks;
                }
            }

            if (collection == null)
            {
                _logger.LogInformation("Collection is null. No summary available.");
                return summaryChunks;
            }

            // Use semantic search to verify ingestion worked, following the example pattern
            // Search with a generic query to get diverse sample content
            string searchQuery = "summary overview content";
            _logger.LogInformation("Performing semantic search on collection 'data' with query: '{Query}', top: {Top}", searchQuery, topResults);

            await foreach (var result in collection.SearchAsync(searchQuery, top: topResults))
            {
                // Access content from the dictionary-like record, as shown in the example
                if (result.Record.TryGetValue("content", out object? contentObj) && contentObj is string content)
                {
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        // Truncate content if too long for display
                        string displayContent = content.Length > 500 ? content[..500] + "..." : content;

                        summaryChunks.Add(new SummaryChunk
                        {
                            Score = result.Score.GetValueOrDefault(0.0),
                            Content = displayContent
                        });
                    }
                }
            }

            _logger.LogInformation("Retrieved {Count} summary chunks from vector store using semantic search", summaryChunks.Count);
        }
        catch (VectorStoreException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
        {
            // Collection doesn't exist (SQLite error 1: no such table/column)
            _logger.LogInformation("Collection 'data' does not exist. No summary available.");
            return summaryChunks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve ingestion summary: {Message}", ex.Message);
            // Don't throw - return empty list instead to allow ingestion to complete
        }

        return summaryChunks;
    }

    private IChatClient GetChatClient(ILoggerFactory loggerFactory)
    {
        string provider = _config.AiProvider.ToLowerInvariant();

        return provider switch
        {
            "openai" => GetOpenAIChatClient(),
            "azureopenai" => GetAzureOpenAIChatClient(loggerFactory),
            "ollama" => GetOllamaChatClient(),
            _ => throw new InvalidOperationException($"Unknown AI provider: {_config.AiProvider}")
        };
    }

    private IChatClient GetOllamaChatClient()
    {
        return new OllamaApiClient(new Uri(_config.OllamaEndpoint), _config.OllamaChatModel);
    }

    private IChatClient GetOpenAIChatClient()
    {
        OpenAIClient client = new(_config.OpenAiApiKey);
        return client.GetChatClient(_config.OpenAiChatModel).AsIChatClient();
    }

    private IChatClient GetAzureOpenAIChatClient(ILoggerFactory loggerFactory)
    {
        ClientLoggingOptions clientLoggingOptions = new()
        {
            EnableLogging = true
        };
        OpenAIClientOptions openAiClientOptions = new()
        {
            Endpoint = new Uri(_config.AzureOpenAiEndpoint),
            ClientLoggingOptions = clientLoggingOptions,
        };

        if (string.IsNullOrWhiteSpace(_config.AzureOpenAiApiKey))
        {
            throw new InvalidOperationException("AZURE_OPENAI_API_KEY must be set for AzureOpenAI provider");
        }

        ApiKeyCredential apiKeyCredential = new(_config.AzureOpenAiApiKey);
        OpenAIClient openAIClient = new(apiKeyCredential, openAiClientOptions);
        return openAIClient.GetChatClient(_config.AzureOpenAiChatDeployment).AsIChatClient();
    }

    private string GetTokenizerModel()
    {
        string provider = _config.AiProvider.ToLowerInvariant();

        return provider switch
        {
            "openai" => _config.OpenAiChatModel,
            "azureopenai" => _config.AzureOpenAiChatDeployment,
            "ollama" => "gpt-4", // TiktokenTokenizer doesn't support Ollama model names, use a compatible default
            _ => "gpt-4" // Default fallback
        };
    }

    private VectorStoreCollectionDefinition CreateCollectionDefinition(int embeddingDimension)
    {
        // Create a definition that matches the schema created by VectorStoreWriter<string>
        // VectorStoreWriter creates collections with Dictionary<string, object?> records
        // The definition needs to specify the key property, vector property, and data properties
        // Based on the actual database schema, the key column is "key" and there are additional columns
        return new VectorStoreCollectionDefinition
        {
            Properties =
            [
                // Key property - VectorStoreWriter<string> uses "key" as the column name
                // Key properties must be one of: int, long, string, Guid
                new VectorStoreKeyProperty("key", typeof(string)),
                // Vector property - stores the embedding vector (stored in vec_data virtual table as "embedding")
                new VectorStoreVectorProperty("embedding", typeof(ReadOnlyMemory<float>), dimensions: embeddingDimension),
                // Data properties - match the actual schema columns
                new VectorStoreDataProperty("content", typeof(string)),
                new VectorStoreDataProperty("context", typeof(string)),
                new VectorStoreDataProperty("documentid", typeof(string))
            ]
        };
    }

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

    private IngestionChunker<string> CreateChunker(ChunkingStrategy strategy,
                                                   IngestionChunkerOptions options,
                                                   IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) => strategy switch
                                                   {
                                                       ChunkingStrategy.Semantic => new SemanticSimilarityChunker(embeddingGenerator, options),

                                                       ChunkingStrategy.Section => new SectionChunker(options),

                                                       ChunkingStrategy.Structure => new HeaderChunker(options),

                                                       _ => throw new ArgumentException($"Unknown chunking strategy: {strategy}")
                                                   };

    /// <summary>
    /// Diagnostic method to inspect the actual SQLite database schema.
    /// This helps identify schema mismatches between VectorStoreWriter and SqliteCollection.
    /// </summary>
    private async Task InspectDatabaseSchemaAsync()
    {
        try
        {
            string dbPath = _config.SqliteDbPath;
            if (!File.Exists(dbPath))
            {
                _logger.LogInformation("Database file does not exist at: {DbPath}", dbPath);
                return;
            }

            _logger.LogInformation("=== Database Schema Inspection ===");
            _logger.LogInformation("Database path: {DbPath}", dbPath);

            string connectionString = $"Data Source={dbPath};Pooling=false";
            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();

            // Load vec0 extension if available (required for SqliteVec)
            try
            {
                SqliteCommand loadExtensionCommand = connection.CreateCommand();
                loadExtensionCommand.CommandText = "SELECT load_extension('vec0');";
                await loadExtensionCommand.ExecuteNonQueryAsync();
                _logger.LogInformation("Loaded vec0 extension successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load vec0 extension (this is expected if vec0 is statically linked): {Message}", ex.Message);
            }

            // Get all tables
            List<string> tables = new();
            SqliteCommand tablesCommand = connection.CreateCommand();
            tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using SqliteDataReader tablesReader = await tablesCommand.ExecuteReaderAsync();
            while (await tablesReader.ReadAsync())
            {
                string tableName = tablesReader.GetString(0);
                tables.Add(tableName);
            }

            _logger.LogInformation("Found {Count} tables: {Tables}", tables.Count, string.Join(", ", tables));

            // Inspect each table's schema
            foreach (string tableName in tables)
            {
                _logger.LogInformation("--- Table: {TableName} ---", tableName);

                // Get column information using PRAGMA
                SqliteCommand pragmaCommand = connection.CreateCommand();
                pragmaCommand.CommandText = $"PRAGMA table_info({tableName});";
                await using SqliteDataReader pragmaReader = await pragmaCommand.ExecuteReaderAsync();

                List<string> columns = new();
                while (await pragmaReader.ReadAsync())
                {
                    int cid = pragmaReader.GetInt32(0);
                    string name = pragmaReader.GetString(1);
                    string type = pragmaReader.GetString(2);
                    int notNull = pragmaReader.GetInt32(3);
                    string defaultValue = pragmaReader.IsDBNull(4) ? "NULL" : pragmaReader.GetString(4);
                    int pk = pragmaReader.GetInt32(5);

                    columns.Add(name);
                    _logger.LogInformation("  Column: {Name} ({Type}) [PK: {Pk}, NotNull: {NotNull}, Default: {Default}]",
                        name, type, pk, notNull, defaultValue);
                }

                _logger.LogInformation("  Total columns: {Count} ({Columns})", columns.Count, string.Join(", ", columns));

                // Get row count (skip for vec0 virtual tables as they may not support COUNT)
                if (!tableName.StartsWith("vec_", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        SqliteCommand countCommand = connection.CreateCommand();
                        countCommand.CommandText = $"SELECT COUNT(*) FROM {tableName};";
                        object? countResult = await countCommand.ExecuteScalarAsync();
                        int rowCount = countResult != null ? Convert.ToInt32(countResult) : 0;
                        _logger.LogInformation("  Row count: {Count}", rowCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("  Could not get row count: {Message}", ex.Message);
                    }
                }
            }

            _logger.LogInformation("=== End Schema Inspection ===");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect database schema: {Message}", ex.Message);
        }
    }

    // public class CodeFileReader : IngestionDocumentReader
    // {
    //     public override Task<IngestionDocument> ReadAsync(Stream source,
    //                                                       string identifier,
    //                                                       string mediaType,
    //                                                       CancellationToken cancellationToken = new())
    //     {
    //         IngestionDocument ingestionDocument = new(identifier);
    //
    //         IngestionDocumentElement ingestionDocumentElement = new IngestionDocumentParagraph(markdown: "");
    //         IngestionDocumentSection ingestionDocumentSection = new()
    //                                                             {
    //                                                                 Text = "", //TODO: get file text from source
    //                                                                 Elements =
    //                                                                 {
    //                                                                     ingestionDocumentElement
    //                                                                 },
    //                                                                 Metadata =
    //                                                                 {
    //                                                                     new KeyValuePair<string, object?>(key: "media_type", value: mediaType)
    //                                                                     // TODO: add other helpful metadata
    //                                                                 },
    //                                                                 PageNumber = 0
    //                                                             };
    //
    //         ingestionDocument.Sections.Add(ingestionDocumentSection);
    //
    //         return Task.FromResult(ingestionDocument);
    //     }
    // }
}