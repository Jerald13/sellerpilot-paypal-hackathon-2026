using System.Text.Json.Serialization;
using SellerPilot.Api;
using SellerPilot.Api.Configuration;

// Optional git-ignored .env at the repo root (dotnet user-secrets works too; see README).
DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSellerPilot(builder.Configuration);
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.MigrateSellerPilotDatabaseAsync();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.Run();
