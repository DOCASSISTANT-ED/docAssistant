using DocAssistant.Api.Data;
using DocAssistant.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

// Scoped like ITenantContext: each request gets an interceptor bound to its own tenant.
builder.Services.AddScoped<TenantConnectionInterceptor>();

builder.Services.AddDbContext<AppDbContext>((services, options) => options
    .UseAppDatabase(connectionString)
    .AddInterceptors(services.GetRequiredService<TenantConnectionInterceptor>()));

builder.Services.AddIdentityModule();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Order matters: identify the caller first, then check what they may access.
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();

app.Run();
