using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceChat.Infrastructure.Configuration;

namespace SourceChat.Features.Config;

internal static class ConfigCommand
{
    public static Command Create(Option<LogLevel> logLevelOption)
    {
        Command command = new(name: "config", description: "Show current configuration");

        command.Add(logLevelOption);

        command.SetAction(async result =>
        {
            LogLevel logLevel = result.GetValue(logLevelOption);

            ServiceCollection collection = ServiceCollectionFactory.Create(logLevel);
            await using ServiceProvider serviceProvider = collection.BuildServiceProvider();

            ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ConfigCommand));
            ConfigurationService config = serviceProvider.GetRequiredService<ConfigurationService>();

            try
            {
                config.DisplayConfiguration();

                Console.WriteLine();
                Console.WriteLine("Note: Configuration is loaded from environment variables.");
                Console.WriteLine("Create a .env file or set environment variables to configure SourceChat.");
                Console.WriteLine("See .env.example for available options.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                logger.LogError(ex, "Config command failed");
            }
        });

        return command;
    }
}