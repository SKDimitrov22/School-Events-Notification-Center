using BusinessLogicLayer2;
using BusinessLogicLayer2.Services;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PresentationLayer1.Services;
using System.Text;

// Entry point (= the reference's main/app.py): load config, wire the layers,
// serve the static UI, expose the API. Skeleton only — features go in per epic.

// Load .env from CWD; fall back one level up for `dotnet run --project` invocations
var envPath = File.Exists(".env") ? ".env" : Path.Combine("..", ".env");
if (File.Exists(envPath))
{
    Env.Load(envPath);
}

var builder = WebApplication.CreateBuilder(args);

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException("Missing required config: DATABASE_URL");

// Wire the layers (Presentation -> Business -> Data).
builder.Services.AddBusinessLayer(databaseUrl);
var jwtSecret = builder.Configuration["JWT_SECRET"]
    ?? "dev-only-insecure-secret-change-before-any-real-deployment";
var jwtIssuer = builder.Configuration["JWT_ISSUER"] ?? "school-events";
var jwtAudience = builder.Configuration["JWT_AUDIENCE"] ?? "school-events";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();

var hostUrl = builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.Configuration["urls"]
    ?? builder.Configuration["MockApiBaseUrl"]
    ?? "http://localhost:5000";

if (hostUrl.Contains(';'))
{
    hostUrl = hostUrl.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
}

builder.WebHost.UseUrls(hostUrl);

builder.Services.AddHttpClient<IApiClient, ApiClient>(client =>
{
    client.BaseAddress = new Uri(hostUrl);
});
builder.Services.AddScoped<IAuthSession, AuthSession>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IApplicationInitializer>().EnsureReadyAsync();
}

app.UseStaticFiles();
app.UseSession();
app.UseStatusCodePagesWithReExecute("/Errors/{0}");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
