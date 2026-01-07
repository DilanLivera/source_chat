using System.CommandLine;
using DotNetEnv;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Clear;
using SourceChat.Features.Config;
using SourceChat.Features.Ingest;
using SourceChat.Features.List;
using SourceChat.Features.Query;
using SourceChat.Features.Shared;
using SourceChat.Infrastructure.Configuration;
using SourceChat.Infrastructure.Parsing;
using SourceChat.Infrastructure.Storage;

Env.Load();

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

ILogger<Program> logger = loggerFactory.CreateLogger<Program>();

try
{
    RootCommand rootCommand = new(description: "SourceChat - AI-Powered Code Documentation Assistant");

    rootCommand.Subcommands.Add(IngestCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(QueryCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(ListCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(ClearCommand.Create(loggerFactory));
    rootCommand.Subcommands.Add(ConfigCommand.Create(loggerFactory));

    return await rootCommand.Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    logger.LogError(ex, "Fatal error occurred");

    return 1;
}