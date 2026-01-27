using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using SourceChat.Infrastructure.Configuration;

namespace SourceChat.Infrastructure.Storage;

// https://learn.microsoft.com/en-us/dotnet/ai/iembeddinggenerator
internal sealed class EmbeddingGeneratorFactory
{
    private readonly ConfigurationService _config;
    private readonly ILogger<EmbeddingGeneratorFactory> _logger;
    private IEmbeddingGenerator<string, Embedding<float>>? _cachedInstance;

    public EmbeddingGeneratorFactory(ConfigurationService config, ILogger<EmbeddingGeneratorFactory> logger)
    {
        _config = config;
        _logger = logger;
    }

    public IEmbeddingGenerator<string, Embedding<float>> Create()
    {
        if (_cachedInstance is not null)
        {
            return _cachedInstance;
        }

        string provider = _config.AiProvider.ToLowerInvariant();

        _cachedInstance = provider switch
        {
            "openai" => CreateOpenAIEmbeddingGenerator(),
            "azureopenai" => CreateAzureOpenAIEmbeddingGenerator(),
            "ollama" => CreateOllamaEmbeddingGenerator(),
            _ => throw new InvalidOperationException($"Unknown AI provider: {_config.AiProvider}")
        };

        _logger.LogInformation("Initialized embedding generator for provider: {Provider}", _config.AiProvider);

        return _cachedInstance;
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOpenAIEmbeddingGenerator()
    {
        OpenAIClient client = new(_config.OpenAiApiKey);

        return client.GetEmbeddingClient(_config.OpenAiEmbeddingModel)
                     .AsIEmbeddingGenerator();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateAzureOpenAIEmbeddingGenerator()
    {
        AzureOpenAIClient client = new(new Uri(_config.AzureOpenAiEndpoint),
                                       new AzureKeyCredential(_config.AzureOpenAiApiKey));

        return client.GetEmbeddingClient(_config.AzureOpenAiEmbeddingDeployment)
                     .AsIEmbeddingGenerator();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOllamaEmbeddingGenerator() => new OllamaApiClient(new Uri(_config.OllamaEndpoint),
                                                                                                                  _config.OllamaEmbeddingModel);
}