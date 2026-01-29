using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.AI;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.FileSystem;
using SourceChat.Infrastructure.Storage;
using IngestionResult = SourceChat.Features.Shared.IngestionResult;

namespace SourceChat.Features.Ingest;

// https://learn.microsoft.com/en-us/dotnet/ai/conceptual/data-ingestion
internal sealed class IngestionService
{
    private const int TopResults = 5;
    private readonly ConfigurationService _config;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IngestionService> _logger;
    private readonly IngestionDocumentReader _reader;
    private readonly FileChangeDetector _changeDetector;
    private readonly SqliteVectorStore _vectorStore;
    private readonly IChatClient _chatClient;

    public IngestionService(
        ConfigurationService config,
        VectorStoreProvider vectorStoreProvider,
        EmbeddingGeneratorFactory embeddingFactory,
        ChatClientFactory chatClientFactory,
        FileChangeDetector changeDetector,
        ILoggerFactory loggerFactory,
        IngestionDocumentReader reader)
    {
        _config = config;
        _vectorStore = vectorStoreProvider.GetVectorStore();
        _changeDetector = changeDetector;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IngestionService>();
        _reader = reader;
        _embeddingGenerator = embeddingFactory.Create();
        _chatClient = chatClientFactory.Create();
    }

    public async Task<Result<IngestionResult>> IngestDirectoryAsync(string path,
                                                                    string patterns,
                                                                    ChunkingStrategy strategy,
                                                                    bool incremental)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogError("Directory does not exist: {Path}", path);

            return Result<IngestionResult>.Failure(IngestionErrors.DirectoryNotFound(path));
        }

        string tokenizerModel = _config.AiProvider.ToLowerInvariant() switch
        {
            "openai" => _config.OpenAiChatModel,
            "azureopenai" => _config.AzureOpenAiChatDeployment,
            "ollama" => "gpt-4", // TiktokenTokenizer doesn't support Ollama model names, use a compatible default
            _ => "gpt-4" // Default fallback
        };

        IngestionChunkerOptions chunkerOptions = new(TiktokenTokenizer.CreateForModel(tokenizerModel))
        {
            MaxTokensPerChunk = _config.MaxTokensPerChunk,
            OverlapTokens = _config.ChunkOverlapTokens,
        };

        IngestionChunker<string> chunker = strategy switch
        {
            ChunkingStrategy.Semantic => new SemanticSimilarityChunker(_embeddingGenerator, chunkerOptions),
            ChunkingStrategy.Section => new SectionChunker(chunkerOptions),
            ChunkingStrategy.Structure => new HeaderChunker(chunkerOptions),
            _ => throw new ArgumentException($"Unknown chunking strategy: {strategy}")
        };

        int embeddingDimension = _config.GetEmbeddingDimension();

        using VectorStoreWriter<string> writer = new(_vectorStore,
                                                     dimensionCount: embeddingDimension,
                                                     new VectorStoreWriterOptions
                                                     {
                                                         CollectionName = "data"
                                                     });

        using IngestionPipeline<string> pipeline = new(_reader,
                                                       chunker,
                                                       writer,
                                                       new IngestionPipelineOptions
                                                       {
                                                           ActivitySourceName = "SourceChat",
                                                       },
                                                       _loggerFactory);



        EnricherOptions enricherOptions = new(_chatClient)
        {
            LoggerFactory = _loggerFactory
        };
        IngestionDocumentProcessor imageAlternativeTextEnricher = new ImageAlternativeTextEnricher(enricherOptions);
        IngestionChunkProcessor<string> summaryEnricher = new SummaryEnricher(enricherOptions);
        pipeline.DocumentProcessors.Add(imageAlternativeTextEnricher);
        pipeline.ChunkProcessors.Add(summaryEnricher);

        DirectoryInfo directory = new(path);
        int filesProcessed = 0;

        _logger.LogInformation("Starting to process files from directory: {Path} with patterns: {Patterns}", path, patterns);

        string[] patternArray = patterns.Split(';', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> matchingFiles = [];

        foreach (string pattern in patternArray)
        {
            string[] files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
            matchingFiles.AddRange(files);
        }

        string matchingFilesString = string.Join(", ", matchingFiles.Select(Path.GetFileName));
        _logger.LogInformation("Found {Count} files matching patterns: {Files}", matchingFiles.Count, matchingFilesString);

        foreach (string pattern in patternArray)
        {
            IAsyncEnumerable<Microsoft.Extensions.DataIngestion.IngestionResult> results = pipeline.ProcessAsync(directory, searchPattern: pattern);

            await foreach (Microsoft.Extensions.DataIngestion.IngestionResult result in results)
            {
                if (result.Exception is not null)
                {
                    _logger.LogError(result.Exception, "Error while processing file: {DocumentId}", result.DocumentId);

                    // Check for dimension mismatch error
                    if (result.Exception is not VectorStoreException { InnerException: SqliteException sqliteEx } ||
                        !sqliteEx.Message.Contains("Dimension mismatch", StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<IngestionResult>.Failure(IngestionErrors.FileProcessingError(result.Exception.Message));
                    }

                    // Extract dimension information from error message
                    // Format: "Dimension mismatch for inserted vector for the "embedding" column. Expected 4096 dimensions but received 384."
                    string errorMessage = sqliteEx.Message;
                    int actualDimension = embeddingDimension;

                    // Try to extract expected dimension from error message
                    Match match = Regex.Match(input: errorMessage,
                                              pattern: @"Expected (\d+) dimensions but received (\d+)");
                    if (!match.Success)
                    {
                        return Result<IngestionResult>.Failure(IngestionErrors.DimensionMismatch("unknown", actualDimension));
                    }

                    string expectedDimension = match.Groups[1].Value;
                    actualDimension = int.Parse(match.Groups[2].Value);

                    return Result<IngestionResult>.Failure(IngestionErrors.DimensionMismatch(expectedDimension, actualDimension));

                }

                if (!result.Succeeded)
                {
                    _logger.LogWarning("Failed to process document: {DocumentId}", result.DocumentId);

                    return Result<IngestionResult>.Failure(IngestionErrors.FileProcessingFailed(result.DocumentId));
                }

                _logger.LogInformation("Successfully completed processing '{DocumentId}'", result.DocumentId);

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
                    _logger.LogInformation("Tracked file: {FilePath}", filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to track file: {FilePath}", filePath);

                    return Result<IngestionResult>.Failure(IngestionErrors.FileTrackingError(filePath, ex.Message));
                }

                filesProcessed++;
            }
        }

        _logger.LogInformation("Finished processing. Files processed: {FilesProcessed}", filesProcessed);

        // Save tracking information to persist file tracking
        try
        {
            _changeDetector.SaveTracking();
            _logger.LogInformation("File tracking saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file tracking");

            return Result<IngestionResult>.Failure(IngestionErrors.FileTrackingSaveError(ex.Message));
        }

        IngestionResult ingestionResult = new()
        {
            FilesProcessed = filesProcessed,
            Errors = 0
        };

        List<SummaryChunk> summaryChunks = [];
        VectorStoreCollection<object, Dictionary<string, object?>> collection = writer.VectorStoreCollection;

        // Use semantic search to verify ingestion worked, following the example pattern
        // Search with a generic query to get diverse sample content
        const string searchQuery = "summary overview content";
        _logger.LogInformation("Performing semantic search on collection 'data' with query: '{Query}', top: {Top}", searchQuery, TopResults);

        try
        {
            await foreach (VectorSearchResult<Dictionary<string, object?>> result in collection.SearchAsync(searchQuery, top: TopResults))
            {
                // Access content from the dictionary-like record, as shown in the example
                if (!result.Record.TryGetValue("content", out object? contentObj) || contentObj is not string content)
                {
                    _logger.LogWarning("Result record missing content field or content is not a string");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning("Result content is null or whitespace");
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
        catch (VectorStoreException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 1 })
        {
            // Collection doesn't exist (SQLite error 1: no such table/column)
            _logger.LogError("Collection 'data' does not exist. Ingestion may have failed.");

            return Result<IngestionResult>.Failure(IngestionErrors.CollectionNotFound());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve ingestion summary: {Message}", ex.Message);

            return Result<IngestionResult>.Failure(IngestionErrors.SummaryRetrievalError(ex.Message));
        }

        return Result<IngestionResult>.Success(ingestionResult);
    }
}