using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Ingest;
using SourceChat.Features.Query;
using SourceChat.Infrastructure.AI;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.FileSystem;
using SourceChat.Infrastructure.Storage;

namespace SourceChat;

/// <summary>
/// Provides service registration functionality for SourceChat.
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// Registers all SourceChat services in a new service collection.
    /// </summary>
    /// <param name="minimumLogLevel">The minimum log level for logging configuration.</param>
    /// <returns>A configured service collection ready to build a service provider.</returns>
    public static ServiceCollection RegisterServices(LogLevel minimumLogLevel)
    {
        ServiceCollection services = new();

        services.AddSingleton<ConfigurationService>()
                .AddSingleton<EmbeddingGeneratorFactory>()
                .AddSingleton<ChatClientFactory>()
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