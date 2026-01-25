using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
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

        // Get chat client based on provider
        IChatClient chatClient = GetChatClient(loggerFactory);

        EnricherOptions enricherOptions = new(chatClient)
        {
            LoggerFactory = loggerFactory
        };

        IngestionDocumentProcessor imageAlternativeTextEnricher = new ImageAlternativeTextEnricher(enricherOptions);

        // Determine tokenizer model based on provider
        string tokenizerModel = GetTokenizerModel();
        IngestionChunkerOptions chunkerOptions = new(TiktokenTokenizer.CreateForModel(tokenizerModel))
        {
            MaxTokensPerChunk = _config.MaxTokensPerChunk,
            OverlapTokens = _config.ChunkOverlapTokens,
        };

        IngestionChunker<string> chunker = CreateChunker(strategy, chunkerOptions, embeddingGenerator);

        IngestionChunkProcessor<string> summaryEnricher = new SummaryEnricher(enricherOptions);

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
                                                       loggerFactory)
        {
            DocumentProcessors = { imageAlternativeTextEnricher },
            ChunkProcessors = { summaryEnricher }
        };

        DirectoryInfo directory = new(path);

        // Use the provided patterns parameter instead of hardcoding
        int filesProcessed = 0;
        int errors = 0;

        try
        {
            await foreach (Microsoft.Extensions.DataIngestion.IngestionResult result in pipeline.ProcessAsync(directory, searchPattern: patterns))
            {
                _logger.LogInformation("Completed processing '{DocumentId}'. Succeeded: '{Succeeded}'.", result.DocumentId, result.Succeeded);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed.");
            throw;
        }

        IngestionResult ingestionResult = new()
        {
            FilesProcessed = filesProcessed,
            Errors = errors
        };
        // TotalChunks would need additional tracking to be accurate

        return ingestionResult;
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