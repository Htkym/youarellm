IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args)!;

var web = builder.AddProject<Projects.YouAreLlm_Web>("youarellm-web");
var providerBaseUrl = ReferenceExpression.Create($"{web.GetEndpoint("http")}/v1");

#pragma warning disable ASPIRETERMINAL001

builder.AddExecutable("copilot-completions", "copilot", "..\\..", "--model", "human")
    .WithEnvironment("COPILOT_PROVIDER_BASE_URL", providerBaseUrl)
    .WithEnvironment("COPILOT_PROVIDER_TYPE", "openai")
    .WithEnvironment("COPILOT_MODEL", "human")
    .WaitFor(web)
    .WithTerminal();

builder.AddExecutable("copilot-responses", "copilot", "..\\..", "--model", "human")
    .WithEnvironment("COPILOT_PROVIDER_BASE_URL", providerBaseUrl)
    .WithEnvironment("COPILOT_PROVIDER_TYPE", "openai")
    .WithEnvironment("COPILOT_MODEL", "human")
    .WithEnvironment("COPILOT_PROVIDER_WIRE_API", "responses")
    .WaitFor(web)
    .WithTerminal();

#pragma warning restore ASPIRETERMINAL001

builder.Build().Run();
