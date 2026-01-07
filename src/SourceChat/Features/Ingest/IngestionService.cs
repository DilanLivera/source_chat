using Microsoft.Extensions.AI;
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
                                                            bool verbose,
                                                            bool incremental)
    {
        ProgressReporter progress = new(verbose);
        IngestionResult result = new();

        try
        {
            List<string> files = GetFiles(path, patterns);

            if (files.Count == 0)
            {
                _logger.LogWarning("No files found matching patterns: {Patterns}", patterns);

                return result;
            }

            if (incremental)
            {
                files = FilterChangedFiles(files, verbose);
                if (files.Count == 0)
                {
                    Console.WriteLine("No changed files detected. Ingestion skipped.");

                    return result;
                }
            }

            progress.Start(files.Count);

            foreach (string file in files)
            {
                try
                {
                    FileInfo fileInfo = new(file);
                    int chunksCreated = await ProcessFileAsync(file, strategy, verbose);

                    result.FilesProcessed++;
                    result.TotalChunks += chunksCreated;

                    progress.ReportFileProgress(Path.GetFileName(file), chunksCreated);

                    string hash = await _changeDetector.GetFileHashAsync(file);
                    _changeDetector.UpdateFileTracking(file, fileInfo.LastWriteTimeUtc, hash);
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    progress.ReportError(Path.GetFileName(file), ex);
                    _logger.LogError(ex, "Error processing file: {File}", file);
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

    private async Task<int> ProcessFileAsync(string filePath, ChunkingStrategy strategy, bool verbose)
    {
        IFileParser? parser = _parsers.FirstOrDefault(p => p.CanParse(filePath));
        if (parser == null)
        {
            if (verbose)
            {
                Console.WriteLine($"  No parser found for {Path.GetFileName(filePath)}, skipping.");
            }

            return 0;
        }

        (string content, Dictionary<string, string> metadata) = await parser.ParseAsync(filePath);

        FileInfo fileInfo = new(filePath);
        metadata["file_path"] = filePath;
        metadata["file_name"] = fileInfo.Name;
        metadata["file_size"] = fileInfo.Length.ToString();
        metadata["last_modified"] = fileInfo.LastWriteTimeUtc.ToString("O");

        IngestionDocumentSection section = new(content)
        {
            // Metadata = metadata // TODO: how can we set metadata
        };
        IngestionDocument document = new(identifier: filePath);
        document.Sections.Add(section);

        IngestionChunker<string> chunker = CreateChunker(strategy);

        IAsyncEnumerable<IngestionChunk<string>> chunks = chunker.ProcessAsync(document);

        await StoreChunksAsync(document.Identifier, chunks);

        return await chunks.CountAsync();
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

    private async Task StoreChunksAsync(string documentId, IAsyncEnumerable<IngestionChunk<string>> chunks)
    {
        SqliteVectorStore vectorStore = _vectorStoreManager.GetVectorStore();

        using VectorStoreWriter<string> writer = new(vectorStore, dimensionCount: 1536);

        await writer.WriteAsync(chunks);
    }

    private List<string> GetFiles(string path, string patterns)
    {
        List<string> patternList = patterns.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(p => p.Trim())
                                           .ToList();

        List<string> files = [];

        foreach (string pattern in patternList)
        {
            files.AddRange(Directory.GetFiles(path, pattern, SearchOption.AllDirectories));
        }

        return files.Distinct().OrderBy(f => f).ToList();
    }

    private List<string> FilterChangedFiles(List<string> files, bool verbose)
    {
        List<string> changedFiles = [];

        foreach (string file in files)
        {
            FileInfo fileInfo = new(file);

            if (_changeDetector.HasFileChanged(file, fileInfo.LastWriteTimeUtc))
            {
                changedFiles.Add(file);
            }
            else if (verbose)
            {
                Console.WriteLine($"Skipping unchanged file: {Path.GetFileName(file)}");
            }
        }

        HashSet<string> currentFiles = files.Select(Path.GetFullPath).ToHashSet();
        List<string> trackedFiles = _changeDetector.GetTrackedFiles();

        IEnumerable<string> nonCurrentTrackedFiles = trackedFiles.Where(file => !currentFiles.Contains(file));
        foreach (string file in nonCurrentTrackedFiles)
        {
            if (verbose)
            {
                Console.WriteLine($"Removing deleted file from index: {file}");
            }
            _changeDetector.RemoveFileTracking(file);
            // TODO: Remove chunks from vector store
        }

        return changedFiles;
    }
}