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
using SourceChat.Infrastructure.Storage;
using IngestionResult = SourceChat.Features.Shared.IngestionResult;

namespace SourceChat.Features.Ingest;

// https://learn.microsoft.com/en-us/dotnet/ai/conceptual/data-ingestion
internal class IngestionService
{
    private const int TopResults = 5;
    private readonly ConfigurationService _config;
    private readonly VectorStoreManager _vectorStoreManager;
    private readonly FileChangeDetector _changeDetector;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        ConfigurationService config,
        VectorStoreManager vectorStoreManager,
        FileChangeDetector changeDetector,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _vectorStoreManager = vectorStoreManager;
        _changeDetector = changeDetector;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IngestionService>();

    }

    public async Task<IngestionResult> IngestDirectoryAsync(string path,
                                                            string patterns,
                                                            ChunkingStrategy strategy,
                                                            bool incremental)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogError("Directory does not exist: {Path}", path);

            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        IngestionDocumentReader reader = new MarkdownReader();

        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = _vectorStoreManager.GetEmbeddingGenerator();

        IChatClient chatClient = GetChatClient();

        EnricherOptions enricherOptions = new(chatClient)
        {
            LoggerFactory = _loggerFactory
        };
        IngestionDocumentProcessor imageAlternativeTextEnricher = new ImageAlternativeTextEnricher(enricherOptions);
        IngestionChunkProcessor<string> summaryEnricher = new SummaryEnricher(enricherOptions);

        string tokenizerModel = GetTokenizerModel();
        IngestionChunkerOptions chunkerOptions = new(TiktokenTokenizer.CreateForModel(tokenizerModel))
        {
            MaxTokensPerChunk = _config.MaxTokensPerChunk,
            OverlapTokens = _config.ChunkOverlapTokens,
        };

        IngestionChunker<string> chunker = CreateChunker(strategy, chunkerOptions, embeddingGenerator);

        SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

        int embeddingDimension = GetEmbeddingDimension();

        using VectorStoreWriter<string> writer = new(vectorStore,
                                                     dimensionCount: embeddingDimension,
                                                     new VectorStoreWriterOptions
                                                     {
                                                         CollectionName = "data"
                                                     });

        using IngestionPipeline<string> pipeline = new(reader,
                                                       chunker,
                                                       writer,
                                                       new IngestionPipelineOptions
                                                       {
                                                           ActivitySourceName = "SourceChat",
                                                       },
                                                       _loggerFactory);

        pipeline.DocumentProcessors.Add(imageAlternativeTextEnricher);
        pipeline.ChunkProcessors.Add(summaryEnricher);

        DirectoryInfo directory = new(path);

        int filesProcessed = 0;
        int errors = 0;

        try
        {
            _logger.LogInformation("Starting to process files from directory: {Path} with patterns: {Patterns}", path, patterns);

            string[] patternArray = patterns.Split(';',
                                                   options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<string> matchingFiles = [];

            foreach (string pattern in patternArray)
            {
                string[] files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
                matchingFiles.AddRange(files);
            }

            string matchingFilesString = string.Join(", ", matchingFiles.Select(Path.GetFileName));
            _logger.LogInformation("Found {Count} files matching patterns: {Files}", matchingFiles.Count, matchingFilesString);

            int resultCount = 0;

            foreach (string pattern in patternArray)
            {
                IAsyncEnumerable<Microsoft.Extensions.DataIngestion.IngestionResult> results = pipeline.ProcessAsync(directory, searchPattern: pattern);

                await foreach (Microsoft.Extensions.DataIngestion.IngestionResult result in results)
                {
                    if (result.Exception is not null)
                    {
                        errors++;
                        _logger.LogError(result.Exception, "Error while processing file: {FilePath}", result.Exception.Message);

                        continue;
                    }

                    if (!result.Succeeded)
                    {
                        errors++;
                        _logger.LogWarning("Failed to process document: {DocumentId}", result.DocumentId);

                        continue;
                    }

                    resultCount++;
                    _logger.LogInformation("Completed processing '{DocumentId}'. Succeeded: '{Succeeded}'.", result.DocumentId, result.Succeeded);

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

                    filesProcessed++;
                }
            }

            _logger.LogInformation("Finished processing. Results received: {ResultCount}, Files processed: {FilesProcessed}, Errors: {Errors}", resultCount, filesProcessed, errors);
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

        if (errors != 0)
        {
            return ingestionResult;
        }

        try
        {
            List<SummaryChunk> summaryChunks = [];
            VectorStoreCollection<object, Dictionary<string, object?>> collection = writer.VectorStoreCollection;

            // Use semantic search to verify ingestion worked, following the example pattern
            // Search with a generic query to get diverse sample content
            string searchQuery = "summary overview content";
            _logger.LogInformation("Performing semantic search on collection 'data' with query: '{Query}', top: {Top}", searchQuery, TopResults);

            await foreach (VectorSearchResult<Dictionary<string, object?>> result in collection.SearchAsync(searchQuery, top: TopResults))
            {
                // Access content from the dictionary-like record, as shown in the example
                if (!result.Record.TryGetValue("content", out object? contentObj) || contentObj is not string content)
                {
                    // todo: log
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    // todo: log
                    continue;
                }

                // Truncate content if too long for display
                string displayContent = content.Length > 500 ? content[..500] + "..." : content;

                summaryChunks.Add(new SummaryChunk
                {
                    Score = result.Score.GetValueOrDefault(0.0),
                    Content = displayContent
                });
            }

            ingestionResult.SummaryChunks = summaryChunks;

            _logger.LogInformation("Retrieved {Count} summary chunks from vector store using semantic search", summaryChunks.Count);
        }
        catch (VectorStoreException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 1)
        {
            // Collection doesn't exist (SQLite error 1: no such table/column)
            _logger.LogInformation("Collection 'data' does not exist. No summary available.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve ingestion summary: {Message}", ex.Message);
        }

        return ingestionResult;
    }

    private IChatClient GetChatClient()
    {
        string provider = _config.AiProvider.ToLowerInvariant();

        return provider switch
        {
            "openai" => GetOpenAIChatClient(),
            "azureopenai" => GetAzureOpenAIChatClient(),
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

    private IChatClient GetAzureOpenAIChatClient()
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

}