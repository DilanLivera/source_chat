using Microsoft.Extensions.Logging;

namespace SourceChat.Infrastructure.Configuration;

internal class ConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;

    public ConfigurationService(ILogger<ConfigurationService> logger) => _logger = logger;

    public string AiProvider => GetEnvVar("AI_PROVIDER", "OpenAI");
    public string SqliteDbPath => GetEnvVar("SQLITE_DB_PATH", "./sourcechat.db");

    // OpenAI
    public string OpenAiApiKey => GetEnvVar("OPENAI_API_KEY", "");
    public string OpenAiChatModel => GetEnvVar("OPENAI_CHAT_MODEL", "gpt-4");
    public string OpenAiEmbeddingModel => GetEnvVar("OPENAI_EMBEDDING_MODEL", "text-embedding-3-small");

    // Azure OpenAI
    public string AzureOpenAiEndpoint => GetEnvVar("AZURE_OPENAI_ENDPOINT", "");
    public string AzureOpenAiApiKey => GetEnvVar("AZURE_OPENAI_API_KEY", "");
    public string AzureOpenAiChatDeployment => GetEnvVar("AZURE_OPENAI_CHAT_DEPLOYMENT", "gpt-4");
    public string AzureOpenAiEmbeddingDeployment => GetEnvVar("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small");

    // Ollama
    public string OllamaEndpoint => GetEnvVar("OLLAMA_ENDPOINT", "http://localhost:11434");
    public string OllamaChatModel => GetEnvVar("OLLAMA_CHAT_MODEL", "llama3.2");
    public string OllamaEmbeddingModel => GetEnvVar("OLLAMA_EMBEDDING_MODEL", "all-minilm");

    // Chunking
    public int MaxTokensPerChunk => int.Parse(GetEnvVar("MAX_TOKENS_PER_CHUNK", "2000"));
    public int ChunkOverlapTokens => int.Parse(GetEnvVar("CHUNK_OVERLAP_TOKENS", "200"));

    public int GetEmbeddingDimension()
    {
        string provider = AiProvider.ToLowerInvariant();
        string embeddingModel = provider switch
        {
            "openai" => OpenAiEmbeddingModel,
            "azureopenai" => AzureOpenAiEmbeddingDeployment,
            "ollama" => OllamaEmbeddingModel,
            _ => "text-embedding-3-small"
        };

        // Return dimension based on model
        return embeddingModel.ToLowerInvariant() switch
        {
            "cohere-embed-v3-english" => 1024,
            "text-embedding-3-small" => 1536,
            "text-embedding-3-large" => 3072,
            "text-embedding-ada-002" => 1536,
            "all-minilm" => 384, // Ollama's all-minilm model
            "qwen3-embedding" => 4096, // Qwen3 embedding model has 4096 dimensions
            _ => 1536 // Default fallback
        };
    }

    private string GetEnvVar(string key, string defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            _logger.LogDebug("Environment variable {Key} not set, using default: {Default}", key, defaultValue);

            return defaultValue;
        }

        return value;
    }

    public void Validate()
    {
        string provider = AiProvider.ToLowerInvariant();

        switch (provider)
        {
            case "openai":
                if (string.IsNullOrWhiteSpace(OpenAiApiKey))
                    throw new InvalidOperationException("OPENAI_API_KEY must be set for OpenAI provider");

                break;
            case "azureopenai":
                if (string.IsNullOrWhiteSpace(AzureOpenAiEndpoint) || string.IsNullOrWhiteSpace(AzureOpenAiApiKey))
                    throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY must be set for AzureOpenAI provider");

                break;
            case "ollama":
                if (string.IsNullOrWhiteSpace(OllamaEndpoint))
                    throw new InvalidOperationException("OLLAMA_ENDPOINT must be set for Ollama provider");

                break;
            default:
                throw new InvalidOperationException($"Unknown AI provider: {AiProvider}. Valid options: OpenAI, AzureOpenAI, Ollama");
        }
    }

    public void DisplayConfiguration()
    {
        Console.WriteLine("Current Configuration:");
        Console.WriteLine($"  AI Provider: {AiProvider}");
        Console.WriteLine($"  Database Path: {SqliteDbPath}");
        Console.WriteLine($"  Max Tokens Per Chunk: {MaxTokensPerChunk}");
        Console.WriteLine($"  Chunk Overlap: {ChunkOverlapTokens}");

        switch (AiProvider.ToLowerInvariant())
        {
            case "openai":
                Console.WriteLine($"  Chat Model: {OpenAiChatModel}");
                Console.WriteLine($"  Embedding Model: {OpenAiEmbeddingModel}");
                Console.WriteLine($"  API Key: {MaskApiKey(OpenAiApiKey)}");

                break;
            case "azureopenai":
                Console.WriteLine($"  Endpoint: {AzureOpenAiEndpoint}");
                Console.WriteLine($"  Chat Deployment: {AzureOpenAiChatDeployment}");
                Console.WriteLine($"  Embedding Deployment: {AzureOpenAiEmbeddingDeployment}");
                Console.WriteLine($"  API Key: {MaskApiKey(AzureOpenAiApiKey)}");

                break;
            case "ollama":
                Console.WriteLine($"  Endpoint: {OllamaEndpoint}");
                Console.WriteLine($"  Chat Model: {OllamaChatModel}");
                Console.WriteLine($"  Embedding Model: {OllamaEmbeddingModel}");

                break;
        }
    }

    private string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "[NOT SET]";
        if (apiKey.Length <= 8)
            return "****";

        return apiKey[..4] + "****" + apiKey[^4..];
    }
}