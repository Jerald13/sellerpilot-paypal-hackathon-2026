using System.Text.Json.Serialization;
using SellerPilot.Api;
using SellerPilot.Api.Auth;
using SellerPilot.Api.Configuration;

// Optional git-ignored .env at the repo root (dotnet user-secrets works too; see README).
DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSellerPilot(builder.Configuration);
builder.Services.AddSellerPilotAuth();
builder.Services.AddSingleton<SellerPilot.Api.Evaluation.EvalProcessRunner>();
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.MigrateSellerPilotDatabaseAsync();
await app.Services.SeedSellerPilotShopAsync();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Hosted build: the React app is published into wwwroot and served from the same origin as the API
// (so the SameSite=Strict cookie works). In local dev, Vite serves the UI instead and wwwroot is absent.
var hasWebUi = File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html"));
if (hasWebUi)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
if (hasWebUi)
{
    // Client-side routes such as /shop get index.html. File paths (/assets/*.js) are left to the static files,
    // and unknown /api paths still return 404.
    app.MapFallbackToFile("{*path:nonfile:regex(^(?!api/).*$)}", "index.html");
}

app.Run();
