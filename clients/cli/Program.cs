using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("scaffolder");
    config.SetApplicationVersion("0.1.0");
});
return app.Run(args);
