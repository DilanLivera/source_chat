using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using SourceChat.Infrastructure.AI;
using SourceChat.Infrastructure.Configuration;

namespace SourceChat.Infrastructure.Storage;

internal sealed class VectorStoreProvider : IDisposable
{
    private readonly ConfigurationService _config;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<VectorStoreProvider> _logger;
    private SqliteVectorStore? _vectorStore;

    public VectorStoreProvider(
        EmbeddingGeneratorFactory embeddingFactory,
        ConfigurationService config,
        ILogger<VectorStoreProvider> logger)
    {
        _config = config;
        _logger = logger;
        _embeddingGenerator = embeddingFactory.Create();
    }

    public SqliteVectorStore GetVectorStore()
    {
        if (_vectorStore is not null)
        {
            return _vectorStore;
        }

        string connectionString = $"Data Source={_config.SqliteDbPath};Pooling=false";
        SqliteVectorStoreOptions sqliteVectorStoreOptions = new()
        {
            EmbeddingGenerator = _embeddingGenerator
        };
        _vectorStore = new SqliteVectorStore(connectionString, sqliteVectorStoreOptions);

        _logger.LogInformation("Initialized SQLite vector store at {Path}", _config.SqliteDbPath);

        return _vectorStore;
    }

    public void Dispose()
    {
        _vectorStore?.Dispose();
    }
}