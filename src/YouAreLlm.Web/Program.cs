using YouAreLlm.Web.Components;
using YouAreLlm.Core;
using YouAreLlm.Web.Api;
using YouAreLlm.Web.Mcp;
using YouAreLlm.Web.Research;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddSource(CopilotRequestTelemetry.ActivitySourceName);

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter();
        }
    });

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();
builder.Services.Configure<ResearchCaptureOptions>(
    builder.Configuration.GetSection(ResearchCaptureOptions.SectionName));
builder.Services.AddSingleton<IRawPromptArchive, RawPromptArchive>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<PendingRequestStore>();
builder.Services.AddSingleton<TokenUsageEstimator>();
builder.Services.AddSingleton<IFleetRunStore, FleetRunStore>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseCors();
app.UseAntiforgery();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapOpenAiEndpoints();
app.MapMcp("/mcp");
app.MapGet("/dashboard/consolelogs/{resourceName}", RedirectToDashboardConsoleLogs)
    .ExcludeFromDescription();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static IResult RedirectToDashboardConsoleLogs(HttpRequest request, string resourceName)
{
    if (!Uri.TryCreate(request.Headers.Referer, UriKind.Absolute, out var dashboardUri) ||
        (dashboardUri.Scheme != Uri.UriSchemeHttp && dashboardUri.Scheme != Uri.UriSchemeHttps))
    {
        return TypedResults.BadRequest();
    }

    var dashboardBaseUrl = dashboardUri.GetLeftPart(UriPartial.Authority);
    var consoleLogPath = $"/consolelogs/resource/{Uri.EscapeDataString(resourceName)}";
    return TypedResults.Redirect($"{dashboardBaseUrl}{consoleLogPath}");
}

public partial class Program;
