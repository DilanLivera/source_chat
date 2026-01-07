using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OllamaSharp;
using OpenAI;
using SourceChat.Infrastructure.Configuration;

namespace SourceChat.Infrastructure.Storage;

// https://learn.microsoft.com/en-us/dotnet/ai/iembeddinggenerator
internal class VectorStoreManager : IDisposable
{
    private readonly ConfigurationService _config;
    private readonly ILogger<VectorStoreManager> _logger;
    private SqliteVectorStore? _vectorStore;
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;

    public VectorStoreManager(ConfigurationService config, ILogger<VectorStoreManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    public SqliteVectorStore GetVectorStore()
    {
        if (_vectorStore is not null)
        {
            return _vectorStore;
        }

        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = GetEmbeddingGenerator();

        SqliteVectorStoreOptions sqliteVectorStoreOptions = new()
        {
            EmbeddingGenerator = embeddingGenerator
        };
        string connectionString = $"Data Source={_config.SqliteDbPath};Pooling=false";
        _vectorStore = new SqliteVectorStore(connectionString, sqliteVectorStoreOptions);

        _logger.LogInformation("Initialized SQLite vector store at {Path}", _config.SqliteDbPath);

        return _vectorStore;
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator()
    {
        if (_embeddingGenerator is not null)
        {
            return _embeddingGenerator;
        }

        string provider = _config.AiProvider.ToLowerInvariant();

        _embeddingGenerator = provider switch
        {
            "openai" => CreateOpenAIEmbeddingGenerator(),
            "azureopenai" => CreateAzureOpenAIEmbeddingGenerator(),
            "ollama" => CreateOllamaEmbeddingGenerator(),
            _ => throw new InvalidOperationException($"Unknown AI provider: {_config.AiProvider}")
        };

        _logger.LogInformation("Initialized embedding generator for provider: {Provider}", _config.AiProvider);

        return _embeddingGenerator;
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOpenAIEmbeddingGenerator()
    {
        OpenAIClient client = new(_config.OpenAiApiKey);

        return client.GetEmbeddingClient(_config.OpenAiEmbeddingModel)
                     .AsIEmbeddingGenerator();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateAzureOpenAIEmbeddingGenerator()
    {
        ChatCompletionsClient client = new(new Uri(_config.AzureOpenAiEndpoint),
                                           new Azure.AzureKeyCredential(_config.AzureOpenAiApiKey));

        // TODO: Azure implementation may need adjustment based on SDK version
        throw new NotImplementedException("Azure OpenAI embedding generator needs SDK-specific implementation");
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOllamaEmbeddingGenerator() => new OllamaApiClient(new Uri(_config.OllamaEndpoint));

    public void Dispose()
    {
        _vectorStore?.Dispose();
    }
}