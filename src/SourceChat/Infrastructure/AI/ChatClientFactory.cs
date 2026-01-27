using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using SourceChat.Infrastructure.Configuration;

namespace SourceChat.Infrastructure.AI;

internal sealed class ChatClientFactory
{
    private readonly ConfigurationService _config;
    private readonly ILogger<ChatClientFactory> _logger;
    private IChatClient? _cachedInstance;

    public ChatClientFactory(ConfigurationService config, ILogger<ChatClientFactory> logger)
    {
        _config = config;
        _logger = logger;
    }

    public IChatClient Create()
    {
        if (_cachedInstance is not null)
        {
            return _cachedInstance;
        }

        string provider = _config.AiProvider.ToLowerInvariant();

        _cachedInstance = provider switch
        {
            "openai" => CreateOpenAIChatClient(),
            "azureopenai" => CreateAzureOpenAIChatClient(),
            "ollama" => CreateOllamaChatClient(),
            _ => throw new InvalidOperationException($"Unknown AI provider: {_config.AiProvider}")
        };

        _logger.LogInformation("Initialized chat client for provider: {Provider}", _config.AiProvider);

        return _cachedInstance;
    }

    private IChatClient CreateOpenAIChatClient()
    {
        OpenAIClient client = new(_config.OpenAiApiKey);
        return client.GetChatClient(_config.OpenAiChatModel).AsIChatClient();
    }

    private IChatClient CreateAzureOpenAIChatClient()
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
        OpenAIClient openAiClient = new(apiKeyCredential, openAiClientOptions);
        return openAiClient.GetChatClient(_config.AzureOpenAiChatDeployment)
                           .AsIChatClient();
    }

    private IChatClient CreateOllamaChatClient() => new OllamaApiClient(new Uri(_config.OllamaEndpoint),
                                                                        _config.OllamaChatModel);
}