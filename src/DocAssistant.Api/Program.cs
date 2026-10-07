using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Tenants;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// Errors as RFC 9457 ProblemDetails; request DTOs validated from their data annotations.
builder.Services.AddProblemDetails();
builder.Services.AddValidation();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

// Scoped like ITenantContext: each request gets an interceptor bound to its own tenant.
builder.Services.AddScoped<TenantConnectionInterceptor>();

builder.Services.AddDbContext<AppDbContext>((services, options) => options
    .UseAppDatabase(connectionString)
    .AddInterceptors(services.GetRequiredService<TenantConnectionInterceptor>()));

builder.Services.AddIdentityModule();
builder.Services.AddTenantsModule();
builder.Services.AddDocumentsModule();
builder.Services.AddIngestionModule();

// The Angular app is served from another origin (docs/decisions.md #41). Browsers only let
// it call this API from the origins listed here; with none configured, none are allowed.
// No AllowCredentials: the access token travels in the Authorization header, not a cookie.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Before authentication: a browser's preflight (OPTIONS) request carries no token.
app.UseCors();

// Order matters: identify the caller first, then check what they may access.
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();

app.Run();
