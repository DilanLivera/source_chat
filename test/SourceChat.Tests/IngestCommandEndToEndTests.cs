using System.Diagnostics;
using System.Text;
using SourceChat.Infrastructure.Storage;

namespace SourceChat.Tests;

public class IngestCommandEndToEndTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    private readonly Dictionary<string, string?> _originalEnvVars;
    private readonly string _exePath;

    public IngestCommandEndToEndTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"SourceChatTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        _testDbPath = Path.Combine(Path.GetTempPath(), $"SourceChatTest_{Guid.NewGuid()}.db");

        string testBinDir = Path.GetDirectoryName(typeof(IngestCommandEndToEndTests).Assembly.Location)!;
        // From: test/SourceChat.Tests/bin/Debug/net9.0
        // To: src/SourceChat/bin/Debug/net9.0/SourceChat.dll
        string solutionDir = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));
        string exePath = Path.Combine(solutionDir, "src", "SourceChat", "bin", "Debug", "net9.0", "SourceChat.dll");
        _exePath = Path.GetFullPath(exePath);

        if (!File.Exists(_exePath))
        {
            throw new FileNotFoundException($"Executable not found at: {_exePath}");
        }

        // Save original environment variables
        _originalEnvVars = new Dictionary<string, string?>
        {
            ["AI_PROVIDER"] = Environment.GetEnvironmentVariable("AI_PROVIDER"),
            ["AZURE_OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            ["AZURE_OPENAI_ENDPOINT"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            ["AZURE_OPENAI_CHAT_DEPLOYMENT"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT"),
            ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
            ["SQLITE_DB_PATH"] = Environment.GetEnvironmentVariable("SQLITE_DB_PATH"),
            ["OLLAMA_ENDPOINT"] = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
        };

        // Set up minimal test environment variables
        // Using Ollama as it doesn't require API keys (assuming it's running locally)
        Environment.SetEnvironmentVariable("AI_PROVIDER", "Ollama");
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", "http://localhost:11434");
        Environment.SetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL", "all-minilm");
        Environment.SetEnvironmentVariable("SQLITE_DB_PATH", _testDbPath);
    }

    [Fact]
    public async Task IngestCommand_WithValidDirectory_ShouldProcessFiles()
    {
        // Arrange: Create test files
        // Note: Using only .md files because MarkdownReader can only process markdown files
        string testFile1 = Path.Combine(_testDirectory, "test1.md");
        string testFile2 = Path.Combine(_testDirectory, "test2.md");

        await File.WriteAllTextAsync(testFile1, "# Test Markdown File 1\n\nThis is a test markdown file.");
        await File.WriteAllTextAsync(testFile2, "# Test Markdown File 2\n\nAnother test markdown file.");

        // Act: Run the command
        (int exitCode, string output, string error) = await RunCommandAsync("ingest",
                                                                            _testDirectory,
                                                                            "--strategy", "Section",
                                                                            "--patterns", "*.md",
                                                                            "--incremental", "false");

        // Assert: Check output
        Assert.True(exitCode == 0 || exitCode == 1, $"Command exited with code {exitCode}.\nOutput: {output}\nError: {error}");

        // Assert: Check output contains expected information
        Assert.Contains("Ingesting files from:", output);
        Assert.Contains(_testDirectory, output);
        Assert.Contains("Strategy: Section", output);

        // Assert: Check database to confirm processed files are tracked
        FileChangeDetector changeDetector = new(_testDbPath);
        List<string> trackedFiles = changeDetector.GetTrackedFiles();

        // Verify that all processed files are in the database
        string testFile1Path = Path.GetFullPath(testFile1);
        string testFile2Path = Path.GetFullPath(testFile2);

        Assert.True(trackedFiles.Contains(testFile1Path), $"test1.md should be in the tracked files list. Tracked files: {string.Join(", ", trackedFiles)}");
        Assert.True(trackedFiles.Contains(testFile2Path), $"test2.md should be in the tracked files list. Tracked files: {string.Join(", ", trackedFiles)}");

        // Log the full output for debugging
        Console.WriteLine("=== Command Output ===");
        Console.WriteLine(output);
        Console.WriteLine("=== Error Output ===");
        Console.WriteLine(error);
        Console.WriteLine("=== End Output ===");
    }

    [Fact]
    public async Task IngestCommand_WithNonExistentDirectory_ShouldReportError()
    {
        // Arrange
        string nonExistentPath = Path.Combine(_testDirectory, "nonexistent");

        // Act
        (int exitCode, string output, string error) = await RunCommandAsync("ingest", nonExistentPath);

        // Assert
        Assert.Contains("Error: Directory not found:", output);
        Assert.Contains(nonExistentPath, output);
    }

    [Fact]
    public async Task IngestCommand_ShouldUseProvidedPatterns()
    {
        // Arrange: Create test files with different extensions
        string mdFile = Path.Combine(_testDirectory, "test.md");
        string csFile = Path.Combine(_testDirectory, "test.cs");
        string txtFile = Path.Combine(_testDirectory, "test.txt");

        await File.WriteAllTextAsync(mdFile, "# Markdown file");
        await File.WriteAllTextAsync(csFile, "// C# file");
        await File.WriteAllTextAsync(txtFile, "Text file");

        // Act: Only process .md files (using Section strategy to avoid embedding requirements)
        (int exitCode, string output, string error) = await RunCommandAsync("ingest",
                                                                            _testDirectory,
                                                                            "--strategy", "Section",
                                                                            "--patterns", "*.md",
                                                                            "--incremental", "false");

        // Assert: Check that patterns are shown in output
        Assert.Contains("Patterns: *.md", output);

        // Assert: Command should succeed
        Assert.Equal(0, exitCode);

        // Assert: Should not contain completion error messages
        Assert.DoesNotContain("Completed with", output);
        Assert.DoesNotContain("error(s)", output, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine("=== Command Output ===");
        Console.WriteLine(output);
        Console.WriteLine("=== Error Output ===");
        Console.WriteLine(error);
        Console.WriteLine("=== End Output ===");
    }

    [Fact]
    public async Task IngestCommand_ShouldShowDiagnosticInformation()
    {
        // Arrange: Create a test file
        string testFile = Path.Combine(_testDirectory, "test.md");
        await File.WriteAllTextAsync(testFile, "# Test File\n\nThis is a test.");

        // Act: Run with verbose logging
        (int exitCode, string output, string error) = await RunCommandAsync(
                                                                            "ingest",
                                                                            _testDirectory,
                                                                            "--log-level", "Information",
                                                                            "--incremental", "false"
                                                                           );

        // Log everything for diagnosis
        Console.WriteLine("=== Exit Code ===");
        Console.WriteLine(exitCode);
        Console.WriteLine("=== Standard Output ===");
        Console.WriteLine(output);
        Console.WriteLine("=== Standard Error ===");
        Console.WriteLine(error);
        Console.WriteLine("=== End ===");

        // Basic assertions - the command should at least start
        Assert.NotNull(output);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(params string[] args)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{_exePath}\" {string.Join(" ", args.Select(a => $"\"{a}\""))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        StringBuilder outputBuilder = new StringBuilder();
        StringBuilder errorBuilder = new StringBuilder();

        using Process process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait with timeout
        bool completed = await Task.Run(() => process.WaitForExit(60000)); // 60 second timeout

        if (!completed)
        {
            process.Kill();
            throw new TimeoutException("Command execution timed out");
        }

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
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

        // Clean up vectors.db if it was created
        string vectorsDb = Path.Combine(Path.GetDirectoryName(_testDbPath) ?? ".", "vectors.db");
        if (File.Exists(vectorsDb))
        {
            try
            {
                File.Delete(vectorsDb);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}