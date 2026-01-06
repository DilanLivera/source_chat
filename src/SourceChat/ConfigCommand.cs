using System.CommandLine;
using Microsoft.Extensions.Logging;

namespace SourceChat;

internal static class ConfigCommand
{
    public static Command Create(ILoggerFactory loggerFactory)
    {
        Command command = new(name: "config", description: "Show current configuration");

        Option<bool> showOption = new(name: "--show")
        {
            Description = "Display configuration",
            DefaultValueFactory = result => true
        };

        command.Add(showOption);

        command.SetAction(result =>
        {
            ILogger logger = loggerFactory.CreateLogger("ConfigCommand");
            ConfigurationService config = new(loggerFactory.CreateLogger<ConfigurationService>());

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