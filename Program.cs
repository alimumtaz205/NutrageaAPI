using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MySqlConnector;
using Nutragea.Api.Data;
using Nutragea.Api.Security;
using Nutragea.Api.Services;
using Nutragea.Commerce.Services;
using System.Threading.RateLimiting;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Db>();
if (args.Contains("--seed-demo")) { await DemoSeeder.Run(builder.Configuration, builder.Environment); return; }
if (args.Contains("--migrate"))
{
    var db = new Db(builder.Configuration);
    await using var c = await db.Open();
    var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "database/101_commerce.sql"));
    foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        if (string.IsNullOrWhiteSpace(statement))
            continue;
        await using var cmd = new MySqlCommand(statement, c);
        await cmd.ExecuteNonQueryAsync();
    }

    Console.WriteLine("Commerce migration 101 installed. Existing Batch Tracker source is unchanged.");
    return;
}

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>() ?? new();
jwt.Validate();
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<CommerceService>();
builder.Services.AddScoped<MediaService>();
builder.Services.AddSingleton<IPaymentProvider, CodPaymentProvider>();
builder.Services.AddHostedService<ReservationExpiry>();
var data = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["Commerce:DataDirectory"] ?? "Data"));
Directory.CreateDirectory(data);
var protection = builder.Services.AddDataProtection().SetApplicationName("Nutragea.Commerce").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
var certPath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certPath))
    protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certPath, builder.Configuration["DataProtection:CertificatePassword"]));
else if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Production requires DataProtection:CertificatePath and protected persistent key storage. See deployment guide.");
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Nutragea Commerce API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new() { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWE" });
    o.AddSecurityRequirement(new() { [new OpenApiSecurityScheme { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenService(jwt).ValidationParameters;
    o.Events = new()
    {
        OnMessageReceived = context =>
        {
            var h = context.Request.Headers.Authorization.ToString();
            if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = h[7..].Trim();
                if (token.Length > 8192 || token.Split('.').Length != 5)
                    context.Fail("JWE required");
                else
                    try
                    {
                        using var header = System.Text.Json.JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[0]));
                        if (header.RootElement.GetProperty("alg").GetString() != SecurityAlgorithms.Aes256KW || header.RootElement.GetProperty("enc").GetString() != SecurityAlgorithms.Aes256CbcHmacSha512)
                            context.Fail("Unsupported JWE");
                    }
                    catch
                    {
                        context.Fail("Invalid JWE");
                    }
            }

            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var p = context.Principal!;
            var sid = p.FindFirst("sid")?.Value;
            var user = p.FindFirst("sub")?.Value;
            var role = p.FindFirst("role")?.Value;
            if (sid == null || !long.TryParse(user, out _) || role == null || !await context.HttpContext.RequestServices.GetRequiredService<AuthService>().ValidateSession(sid, user!, role, context.HttpContext.RequestAborted))
                context.Fail("Session unavailable");
        }
    };

});

builder.Services.AddCors(options =>
{
    options.AddPolicy("NutrageaStore", policy =>
    {
        policy
            .WithOrigins(
                "https://twilight-leaf-d8b7.info-anumora.workers.dev")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("public-write", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 6 * 1024 * 1024;
});
var app = builder.Build();
app.Use(async (c, next) =>
{
    c.Response.Headers.CacheControl = "no-store";
    c.Response.Headers["X-Content-Type-Options"] = "nosniff";
    c.Response.Headers["Referrer-Policy"] = "no-referrer";
    try
    {
        await next(c);
    }
    catch (OperationCanceledException)when (c.RequestAborted.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        if (c.Response.HasStarted)
            throw;
        int status = ex is ApiException known ? known.Status : ex is MySqlException sql && sql.Number == 1062 ? 409 : 500;
        if (status == 500)
            app.Logger.LogError(ex, "Commerce request failed: {TraceId}", c.TraceIdentifier);
        c.Response.Clear();
        c.Response.Headers.CacheControl = "no-store";
        await Results.Problem(statusCode: status, title: status == 500 ? "Server error" : "Request could not be completed", detail: ex is ApiException ? ex.Message : status == 409 ? "A record with this reference already exists." : "Please try again or contact support.", extensions: new Dictionary<string, object?> { { "traceId", c.TraceIdentifier } }).ExecuteAsync(c);
    }
});
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
//if (app.Environment.IsDevelopment())
//{
    app.UseSwagger();
    app.UseSwaggerUI();
//}
app.UseCors("NutrageaStore");

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy"
})).AllowAnonymous();
app.Run();
public partial class Program
{
}
