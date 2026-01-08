using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
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
        ProgressReporter progress = new();
        IngestionResult result = new();

        try
        {
            List<string> files = patterns.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(p => p.Trim())
                                         .SelectMany(p => Directory.GetFiles(path,
                                                                             searchPattern: p,
                                                                             SearchOption.AllDirectories))
                                         .Distinct()
                                         .OrderBy(f => f)
                                         .ToList();

            if (files.Count == 0)
            {
                _logger.LogWarning("No files found matching patterns: {Patterns}", patterns);

                return result;
            }

            if (incremental)
            {
                List<string> changedFiles = [];

                foreach (string file in files)
                {
                    FileInfo fileInfo = new(file);

                    if (_changeDetector.HasFileChanged(file, fileInfo.LastWriteTimeUtc))
                    {
                        changedFiles.Add(file);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping unchanged file: {FileName}", Path.GetFileName(file));
                    }
                }

                List<string> trackedFiles = _changeDetector.GetTrackedFiles();

                HashSet<string> currentFiles = files.Select(Path.GetFullPath)
                                                    .ToHashSet();

                string[] staleFiles = trackedFiles.Where(file => !currentFiles.Contains(file))
                                                  .ToArray();
                foreach (string file in staleFiles)
                {
                    _logger.LogInformation("Removing deleted file from index: {File}", file);
                    _changeDetector.RemoveFileTracking(file);
                    // TODO: Remove chunks from vector store
                }

                files = changedFiles;

                if (files.Count == 0)
                {
                    Console.WriteLine("No changed files detected. Ingestion skipped.");

                    return result;
                }
            }

            progress.Start(files.Count);

            foreach (string filePath in files)
            {
                try
                {
                    IFileParser? parser = _parsers.FirstOrDefault(p => p.CanParse(filePath));

                    if (parser is null)
                    {
                        _logger.LogWarning("No parser found for {FileName}, skipping.", Path.GetFileName(filePath));
                    }
                    else
                    {
                        _logger.LogInformation("Processing {FileName}...", Path.GetFileName(filePath));

                        (string content, Dictionary<string, string> metadata) = await parser.ParseAsync(filePath);

                        FileInfo fileInfo = new(filePath);
                        metadata["file_path"] = filePath;
                        metadata["file_name"] = fileInfo.Name;
                        metadata["file_size"] = fileInfo.Length.ToString();
                        metadata["last_modified"] = fileInfo.LastWriteTimeUtc.ToString(format: "O");

                        IngestionDocumentSection section = new(content)
                        {
                            // Metadata = metadata // TODO: how can we set metadata
                        };
                        IngestionDocument document = new(identifier: filePath);
                        document.Sections.Add(section);

                        IngestionChunker<string> chunker = CreateChunker(strategy);

                        IAsyncEnumerable<IngestionChunk<string>> chunks = chunker.ProcessAsync(document);

                        SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

                        using VectorStoreWriter<string> writer = new(vectorStore, dimensionCount: 1536);

                        await writer.WriteAsync(chunks);

                        int chunksCreated = await chunks.CountAsync();

                        result.FilesProcessed++;
                        result.TotalChunks += chunksCreated;

                        progress.ReportFileProgress(Path.GetFileName(filePath), chunksCreated);

                        string hash = await _changeDetector.GetFileHashAsync(filePath);
                        _changeDetector.UpdateFileTracking(filePath, fileInfo.LastWriteTimeUtc, hash);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    progress.ReportError(Path.GetFileName(filePath), ex);
                    _logger.LogError(ex, "Error processing file: {File}", filePath);
                }
            }

            progress.Complete();

            _changeDetector.SaveTracking();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error during ingestion");

            throw;
        }
    }

    private IngestionChunker<string> CreateChunker(ChunkingStrategy strategy)
    {
        TiktokenTokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");
        IngestionChunkerOptions options = new(tokenizer)
        {
            MaxTokensPerChunk = _config.MaxTokensPerChunk,
            OverlapTokens = _config.ChunkOverlapTokens
        };

        return strategy switch
        {
            ChunkingStrategy.Semantic => new SemanticSimilarityChunker(_vectorStoreManager.GetEmbeddingGenerator(),
                                                                       options),

            ChunkingStrategy.Section => new SectionChunker(options),

            ChunkingStrategy.Structure => new HeaderChunker(options),

            _ => throw new ArgumentException($"Unknown chunking strategy: {strategy}")
        };
    }
}