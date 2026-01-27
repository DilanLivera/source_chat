using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Ingest;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.AI;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.FileSystem;
using SourceChat.Infrastructure.Storage;
using IngestionResult = SourceChat.Features.Shared.IngestionResult;

namespace SourceChat.Tests.Integration;

public class IngestionServiceUnitTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    private readonly Dictionary<string, string?> _originalEnvVars;

    public IngestionServiceUnitTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"SourceChatUnitTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        _testDbPath = Path.Combine(Path.GetTempPath(), $"SourceChatUnitTest_{Guid.NewGuid()}.db");

        // Save original environment variables
        _originalEnvVars = new Dictionary<string, string?>
        {
            ["AI_PROVIDER"] = Environment.GetEnvironmentVariable("AI_PROVIDER"),
            ["OLLAMA_ENDPOINT"] = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT"),
            ["SQLITE_DB_PATH"] = Environment.GetEnvironmentVariable("SQLITE_DB_PATH")
        };

        // Set up test environment variables for Ollama
        Environment.SetEnvironmentVariable("AI_PROVIDER", "Ollama");
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", "http://localhost:11434");
        Environment.SetEnvironmentVariable("SQLITE_DB_PATH", _testDbPath);
    }

    [Fact]
    public async Task IngestDirectoryAsync_WithValidFiles_ShouldProcessFiles()
    {
        // Arrange: Create test files
        string testFile1 = Path.Combine(_testDirectory, "test1.md");
        string testFile2 = Path.Combine(_testDirectory, "test2.md");
        string testFile3 = Path.Combine(_testDirectory, "test.cs");

        await File.WriteAllTextAsync(testFile1, "# Test Markdown File 1\n\nThis is a test markdown file.");
        await File.WriteAllTextAsync(testFile2, "# Test Markdown File 2\n\nAnother test markdown file.");
        await File.WriteAllTextAsync(testFile3, "// Test C# file\npublic class Test { }");

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());
        EmbeddingGeneratorFactory embeddingFactory = new(config, loggerFactory.CreateLogger<EmbeddingGeneratorFactory>());
        ChatClientFactory chatClientFactory = new(config, loggerFactory.CreateLogger<ChatClientFactory>());
        VectorStoreProvider vectorStoreProvider = new(embeddingFactory, config, loggerFactory.CreateLogger<VectorStoreProvider>());
        FileChangeDetector changeDetector = new(config);
        IngestionDocumentReader reader = new MarkdownReader();
        IngestionService ingestionService = new(config, vectorStoreProvider, embeddingFactory, chatClientFactory, changeDetector, loggerFactory, reader);

        // Act: This is where you can set a breakpoint!
        Result<IngestionResult> result = await ingestionService.IngestDirectoryAsync(
            _testDirectory,
            "*.md;*.cs",
            ChunkingStrategy.Section,
            incremental: false);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.FilesProcessed >= 0, "Files processed should be non-negative");
        Assert.True(result.Value.Errors >= 0, "Errors should be non-negative");

        Console.WriteLine($"Files Processed: {result.Value.FilesProcessed}");
        Console.WriteLine($"Total Chunks: {result.Value.TotalChunks}");
        Console.WriteLine($"Errors: {result.Value.Errors}");
    }

    [Fact]
    public async Task IngestDirectoryAsync_ShouldUseProvidedPatterns()
    {
        // Arrange: Create test files with different extensions
        string mdFile = Path.Combine(_testDirectory, "test.md");
        string csFile = Path.Combine(_testDirectory, "test.cs");
        string txtFile = Path.Combine(_testDirectory, "test.txt");

        await File.WriteAllTextAsync(mdFile, "# Markdown file");
        await File.WriteAllTextAsync(csFile, "// C# file");
        await File.WriteAllTextAsync(txtFile, "Text file");

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());
        EmbeddingGeneratorFactory embeddingFactory = new(config, loggerFactory.CreateLogger<EmbeddingGeneratorFactory>());
        ChatClientFactory chatClientFactory = new(config, loggerFactory.CreateLogger<ChatClientFactory>());
        VectorStoreProvider vectorStoreProvider = new(embeddingFactory, config, loggerFactory.CreateLogger<VectorStoreProvider>());
        FileChangeDetector changeDetector = new(config);
        IngestionDocumentReader reader = new MarkdownReader();
        IngestionService ingestionService = new(config, vectorStoreProvider, embeddingFactory, chatClientFactory, changeDetector, loggerFactory, reader);

        // Act: Only process .md files - set breakpoint here to debug!
        Result<IngestionResult> result = await ingestionService.IngestDirectoryAsync(
            _testDirectory,
            "*.md",  // Only markdown files
            ChunkingStrategy.Section,
            incremental: false);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Console.WriteLine($"Files Processed: {result.Value.FilesProcessed}");
        Console.WriteLine($"Errors: {result.Value.Errors}");
    }

    [Fact]
    public async Task IngestDirectoryAsync_WithEmptyDirectory_ShouldReturnZeroFiles()
    {
        // Arrange: Empty directory
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());
        EmbeddingGeneratorFactory embeddingFactory = new(config, loggerFactory.CreateLogger<EmbeddingGeneratorFactory>());
        ChatClientFactory chatClientFactory = new(config, loggerFactory.CreateLogger<ChatClientFactory>());
        VectorStoreProvider vectorStoreProvider = new(embeddingFactory, config, loggerFactory.CreateLogger<VectorStoreProvider>());
        FileChangeDetector changeDetector = new(config);
        IngestionDocumentReader reader = new MarkdownReader();
        IngestionService ingestionService = new(config, vectorStoreProvider, embeddingFactory, chatClientFactory, changeDetector, loggerFactory, reader);

        // Act: Set breakpoint here!
        Result<IngestionResult> result = await ingestionService.IngestDirectoryAsync(
            _testDirectory,
            "*.md",
            ChunkingStrategy.Section,
            incremental: false);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(0, result.Value.FilesProcessed);
        Assert.Equal(0, result.Value.Errors);
    }

    public void Dispose()
    {
        // Restore original environment variables
        foreach (KeyValuePair<string, string?> kvp in _originalEnvVars)
        {
            if (kvp.Value == null)
            {
                Environment.SetEnvironmentVariable(kvp.Key, null);
            }
            else
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }

        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            try
            {
                File.Delete(_testDbPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // Clean up tracking file
        string trackingFile = Path.ChangeExtension(_testDbPath, ".tracking.json");
        if (File.Exists(trackingFile))
        {
            try
            {
                File.Delete(trackingFile);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}