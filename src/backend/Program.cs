using System.Text.Json;
using ShipWithin.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{Environment.GetEnvironmentVariable("SHIP_WITHIN_API_PORT") ?? "4174"}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 18000);
var productOwner = new CopilotProductOwner(Path.Combine(Directory.GetCurrentDirectory(), "data", "copilot-home"));
builder.Services.AddSingleton(productOwner);
builder.Services.AddSingleton(new Workflow(
    Environment.GetEnvironmentVariable("SHIP_WITHIN_DATA") ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "workspace.json"),
    productOwner));
builder.Services.AddSingleton<GitHubRepositories>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    var host = context.Request.Host;
    var origin = context.Request.Headers.Origin.ToString();
    if (!new[] { "localhost", "127.0.0.1" }.Contains(host.Host, StringComparer.OrdinalIgnoreCase) ||
        (origin.Length > 0 && origin != $"http://{host}" && origin != $"http://127.0.0.1:{Environment.GetEnvironmentVariable("SHIP_WITHIN_FRONTEND_PORT") ?? "5173"}"))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "Local requests only." });
        return;
    }
    if (context.Request.Method != "GET" && context.Request.Path != "/api/issues/import" &&
        !(context.Request.Path == "/api/new" && context.Request.ContentLength is null or 0) &&
        context.Request.ContentType?.Split(';')[0] != "application/json")
    {
        context.Response.StatusCode = 415;
        await context.Response.WriteAsJsonAsync(new { error = "Content-Type must be application/json." });
        return;
    }
    try { await next(context); }
    catch (WorkflowException error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
    catch (JsonException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid JSON." });
    }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Workflow request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Operation failed. Check local server diagnostics." });
    }
});

app.MapGet("/api/state", (Workflow workflow) => workflow.Snapshot());
app.MapGet("/api/models", (CopilotProductOwner agent, CancellationToken cancellation) => agent.ListModelsAsync(cancellation));
app.MapPost("/api/repository", (JsonElement input, Workflow workflow) => workflow.SelectRepository(input));
app.MapPut("/api/settings/agents/model", (JsonElement input, Workflow workflow) => workflow.SetAgentModel(input));
app.MapGet("/api/repositories", (string? owner, GitHubRepositories repositories, CancellationToken cancellation) => repositories.ListAsync(owner, cancellation));
app.MapPut("/api/draft", (JsonElement input, Workflow workflow) => workflow.Save(input));
app.MapPost("/api/issues/import", (Workflow workflow, CancellationToken cancellation) => workflow.ImportAsync(cancellation));
app.MapPost("/api/authorize", (JsonElement input, Workflow workflow) => workflow.Authorize(input));
app.MapPost("/api/triage", (JsonElement input, Workflow workflow) => workflow.TriageAsync(input));
app.MapPost("/api/new", async (HttpContext context, Workflow workflow) => workflow.New(
    context.Request.ContentLength is > 0 ? await context.Request.ReadFromJsonAsync<JsonElement>() : default));
app.MapPut("/api/proposal/order", (JsonElement input, Workflow workflow) => workflow.Order(input));
app.MapPost("/api/proposal/apply", (JsonElement input, Workflow workflow, CancellationToken cancellation) => workflow.ApplyAsync(input, cancellation));

app.Run();