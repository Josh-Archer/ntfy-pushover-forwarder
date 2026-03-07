using NtfyPushoverForwarder;
using NtfyPushoverForwarder.Models;

var builder = Host.CreateApplicationBuilder(args);

// Add support for reading from environment variables
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<ForwarderOptions>(builder.Configuration.GetSection("Forwarder"));
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
