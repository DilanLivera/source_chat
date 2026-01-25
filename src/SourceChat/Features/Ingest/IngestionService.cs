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
            IAsyncEnumerable<Microsoft.Extensions.DataIngestion.IngestionResult>? results = null;
            try
            {
                results = pipeline.ProcessAsync(directory, searchPattern: "*.md");
                _logger.LogInformation("Pipeline.ProcessAsync called successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start pipeline processing: {Message}", ex.Message);
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

            _logger.LogInformation("Finished processing. Results received: {ResultCount}, Files processed: {FilesProcessed}, Errors: {Errors}", resultCount, filesProcessed, errors);

            if (resultCount == 0)
            {
                _logger.LogWarning("No results received from pipeline. This might indicate that no files were processed or the pipeline failed silently.");
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
            SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

            // Try to get the collection - it may not exist if no data was written
            SqliteCollection<string, VectorRecord>? collection = null;
            try
            {
                collection = vectorStore.GetCollection<string, VectorRecord>(name: "data");
            }
            catch (VectorStoreException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
            {
                // Collection doesn't exist yet (SQLite error 1: no such table/column)
                _logger.LogInformation("Collection 'data' does not exist yet. No summary available.");
                return summaryChunks;
            }
            catch (VectorStoreException)
            {
                // Other vector store errors
                _logger.LogInformation("Vector store collection not available.");
                return summaryChunks;
            }

            if (collection == null)
            {
                return summaryChunks;
            }

            // Use a generic query to get diverse sample content from the ingested documents
            string searchQuery = "summary overview content";
            List<VectorSearchResult<VectorRecord>> searchResults = await collection.SearchAsync(searchQuery, top: topResults)
                                                                                   .ToListAsync();

            foreach (VectorSearchResult<VectorRecord> result in searchResults)
            {
                summaryChunks.Add(new SummaryChunk
                {
                    Score = result.Score.GetValueOrDefault(0.0),
                    Content = result.Record.Text
                });
            }

            _logger.LogInformation("Retrieved {Count} summary chunks from vector store", summaryChunks.Count);
        }
        catch (VectorStoreException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
        {
            // Collection doesn't exist (SQLite error 1: no such table/column)
            _logger.LogInformation("Collection 'data' does not exist. No summary available.");

            throw;
        }
        catch (VectorStoreException ex)
        {
            // Other vector store errors
            _logger.LogInformation("Vector store collection not available: {Message}", ex.Message);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve ingestion summary: {Message}", ex.Message);

            throw;
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