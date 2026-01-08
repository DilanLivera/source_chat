using System.CommandLine;
using DotNetEnv;
using Microsoft.Extensions.Logging;
using SourceChat.Features.Clear;
using SourceChat.Features.Config;
using SourceChat.Features.Ingest;
using SourceChat.Features.List;
using SourceChat.Features.Query;

Env.Load();

Option<LogLevel> logLevelOption = new(name: "--log-level")
{
    Description = "Set the logging level",
    DefaultValueFactory = _ => LogLevel.Warning
};

RootCommand rootCommand = new(description: "SourceChat - AI-Powered Code Documentation Assistant");
rootCommand.Add(logLevelOption);

rootCommand.Subcommands.Add(IngestCommand.Create(logLevelOption));
rootCommand.Subcommands.Add(QueryCommand.Create(logLevelOption));
rootCommand.Subcommands.Add(ListCommand.Create(logLevelOption));
rootCommand.Subcommands.Add(ClearCommand.Create(logLevelOption));
rootCommand.Subcommands.Add(ConfigCommand.Create(logLevelOption));

try
{
    return await rootCommand.Parse(args)
                            .InvokeAsync();
}
catch (Exception ex)
{
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Error);
    });
    ILogger<Program> logger = loggerFactory.CreateLogger<Program>();
    logger.LogError(ex, "Fatal error occurred");

    return 1;
}