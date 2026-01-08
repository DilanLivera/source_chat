using System.Diagnostics;

namespace SourceChat.Features.Ingest;

internal class ProgressReporter
{
    private readonly Stopwatch _stopwatch = new();
    private int _totalFiles;
    private int _processedFiles;
    private int _totalChunks;
    private readonly bool _verbose;

    public ProgressReporter(bool verbose = false) => _verbose = verbose;

    public void Start(int totalFiles)
    {
        _totalFiles = totalFiles;
        _processedFiles = 0;
        _totalChunks = 0;
        _stopwatch.Restart();

        Console.WriteLine($"Starting ingestion of {totalFiles} files...\n");
    }

    public void ReportFileProgress(string fileName, int chunksCreated)
    {
        _processedFiles++;
        _totalChunks += chunksCreated;

        int percentage = (_processedFiles * 100) / _totalFiles;
        TimeSpan elapsed = _stopwatch.Elapsed;
        double avgTimePerFile = elapsed.TotalSeconds / _processedFiles;
        TimeSpan estimatedRemaining = TimeSpan.FromSeconds(avgTimePerFile * (_totalFiles - _processedFiles));

        if (_verbose)
        {
            Console.WriteLine($"[{_processedFiles}/{_totalFiles}] {fileName}");
            Console.WriteLine($"  Chunks created: {chunksCreated}");
            Console.WriteLine($"  Progress: {percentage}% | Elapsed: {elapsed:mm\\:ss} | ETA: {estimatedRemaining:mm\\:ss}");
        }
        else
        {
            Console.Write($"\rProgress: [{new string('█', percentage / 2)}{new string('░', 50 - percentage / 2)}] {percentage}% ({_processedFiles}/{_totalFiles}) | ETA: {estimatedRemaining:mm\\:ss}");
        }
    }

    public void Complete()
    {
        _stopwatch.Stop();
        Console.WriteLine(_verbose ? "" : "\n");
        Console.WriteLine($"✓ Ingestion complete!");
        Console.WriteLine($"  Files processed: {_processedFiles}");
        Console.WriteLine($"  Total chunks created: {_totalChunks}");
        Console.WriteLine($"  Total time: {_stopwatch.Elapsed:mm\\:ss}");
        Console.WriteLine($"  Average: {_stopwatch.Elapsed.TotalSeconds / _processedFiles:F2}s per file");
    }

    public void ReportError(string fileName, Exception ex)
    {
        Console.WriteLine($"\n✗ Error processing {fileName}: {ex.Message}");
        if (_verbose)
        {
            Console.WriteLine($"  {ex.StackTrace}");
        }
    }
}