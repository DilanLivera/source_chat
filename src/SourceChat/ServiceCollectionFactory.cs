using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Ingest;
using SourceChat.Features.Query;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Storage;

namespace SourceChat;

/// <summary>
/// Factory for creating and configuring service collections for SourceChat.
/// </summary>
internal static class ServiceCollectionFactory
{
    /// <summary>
    /// Creates a new service collection with all SourceChat services registered.
    /// </summary>
    /// <param name="minimumLogLevel">The minimum log level for logging configuration.</param>
    /// <returns>A configured service collection ready to build a service provider.</returns>
    public static ServiceCollection Create(LogLevel minimumLogLevel)
    {
        ServiceCollection services = new();

        services.AddSingleton<ConfigurationService>()
                .AddSingleton<EmbeddingGeneratorFactory>()
                .AddSingleton<VectorStoreProvider>()
                .AddScoped<FileChangeDetector>()
                .AddScoped<IngestionService>()
                .AddScoped<QueryService>()
                .AddScoped<IngestionDocumentReader>(_ => new MarkdownReader())
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(minimumLogLevel);
                });

        return services;
    }
}