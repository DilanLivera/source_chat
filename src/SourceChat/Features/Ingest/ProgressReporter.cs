using System.Diagnostics;

namespace SourceChat.Features.Ingest;

internal class ProgressReporter
{
    private readonly Stopwatch _stopwatch = new();
    private int _totalFiles;
    private int _processedFiles;
    private int _totalChunks;

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

        int percentage = _totalFiles > 0 ? (_processedFiles * 100) / _totalFiles : 100;
        TimeSpan elapsed = _stopwatch.Elapsed;
        double avgTimePerFile = _processedFiles > 0 ? elapsed.TotalSeconds / _processedFiles : 0;
        TimeSpan estimatedRemaining = TimeSpan.FromSeconds(avgTimePerFile * (_totalFiles - _processedFiles));

        Console.Write($"\rProgress: [{new string('█', percentage / 2)}{new string('░', 50 - percentage / 2)}] {percentage}% ({_processedFiles}/{_totalFiles}) | ETA: {estimatedRemaining:mm\\:ss}");
    }

    public void Complete()
    {
        _stopwatch.Stop();
        Console.WriteLine("\n");
        Console.WriteLine($"✓ Ingestion complete!");
        Console.WriteLine($"  Files processed: {_processedFiles}");
        Console.WriteLine($"  Total chunks created: {_totalChunks}");
        Console.WriteLine($"  Total time: {_stopwatch.Elapsed:mm\\:ss}");
        Console.WriteLine($"  Average: {(_processedFiles > 0 ? _stopwatch.Elapsed.TotalSeconds / _processedFiles : 0):F2}s per file");
    }

    public void ReportError(string fileName, Exception ex)
    {
        Console.WriteLine($"\n✗ Error processing {fileName}: {ex.Message}");
    }
}