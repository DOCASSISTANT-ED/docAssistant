using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Chat;
using DocAssistant.Api.Modules.Documents;
using DocAssistant.Api.Modules.Email;
using DocAssistant.Api.Modules.Embeddings;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.Api.Modules.Ingestion;
using DocAssistant.Api.Modules.Search;
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

builder.Services.AddEmailModule();
builder.Services.AddIdentityModule();
builder.Services.AddTenantsModule();
builder.Services.AddDocumentsModule();
builder.Services.AddEmbeddingsModule();
builder.Services.AddIngestionModule();
builder.Services.AddSearchModule();
builder.Services.AddChatModule();

// The Angular app is served from another origin (docs/decisions.md #41). Browsers only let
// it call this API from the origins listed here; with none configured, none are allowed.
// AllowCredentials: the browser sends the session cookie with its requests (decisions #60),
// and only lets a page do that when the API names the page's origin and allows credentials.
// The origins are always listed one by one; a wildcard together with credentials would
// let any site use a signed-in user's session, and ASP.NET Core refuses to start with it.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

// docs/decisions.md #70: the platform admin account is made from the command line only.
// The command runs instead of the web server and exits.
if (args is [PlatformAdmin.CommandName, ..])
{
    return await PlatformAdmin.RunCommandAsync(app.Services, args);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Before authentication: a browser's preflight (OPTIONS) request carries no token.
app.UseCors();

// After CORS, so that the browser can read the rejection; before authentication, so that
// a forged request is turned away without the token being looked at (decisions #60).
app.UseSessionCookieCsrfProtection();

// Order matters: identify the caller first, then check what they may access.
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapInvitationEndpoints();
app.MapMemberEndpoints();
app.MapDocumentEndpoints();
app.MapChatEndpoints();
app.MapConversationEndpoints();
app.MapPlatformCompanyEndpoints();

app.Run();

return 0;
