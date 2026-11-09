using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BaselineX.Api.Features.Identity;

public static class AuthSetup
{
    public const string DevScheme = "Dev";
    public const string DevUserHeader = "X-Dev-User";
    private const string SelectorScheme = "BearerOrDev";

    // Supabase Auth issues the JWTs; this API only validates them.
    // Development with Auth:DevBypass=true: requests without a Bearer token act as X-Dev-User (default Seed.DevUserId).
    public static void AddSupabaseAuth(this WebApplicationBuilder builder)
    {
        var supabaseUrl = builder.Configuration["Supabase:Url"]?.TrimEnd('/') ?? "";
        var jwtSecret = builder.Configuration["Supabase:JwtSecret"];
        var devBypass = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Auth:DevBypass");
        var issuer = $"{supabaseUrl}/auth/v1";

        var auth = builder.Services.AddAuthentication(devBypass ? SelectorScheme : JwtBearerDefaults.AuthenticationScheme);
        auth.AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = issuer,
                ValidAudience = "authenticated",
                NameClaimType = "sub",
            };
            if (!string.IsNullOrEmpty(jwtSecret))
                o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)); // legacy HS256
            else
                o.ConfigurationManager = new JwksConfigurationManager($"{issuer}/.well-known/jwks.json"); // asymmetric signing keys
        });

        if (devBypass)
        {
            auth.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, null);
            auth.AddPolicyScheme(SelectorScheme, null, o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : DevScheme);
        }

        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();
    }

    // Profile is upserted on the first authenticated call (user_profiles.id = JWT sub, no FK to Supabase).
    public static void UseProfileUpsert(this WebApplication app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.UserIdOrNull() is { } id)
        {
            var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
            if (!cache.TryGetValue(("profile", id), out _))
            {
                var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
                var name = DisplayNameFrom(ctx.User);
                var now = DateTime.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO user_profiles (id, display_name, stripe_onboarding_status, created_at, updated_at)
                    VALUES ({id}, {name}, {nameof(StripeOnboardingStatus.NotStarted)}, {now}, {now})
                    ON CONFLICT (id) DO NOTHING
                    """);
                cache.Set(("profile", id), true, TimeSpan.FromHours(1));
            }
        }
        await next();
    });

    private static string DisplayNameFrom(ClaimsPrincipal user)
    {
        string? name = null;
        if (user.FindFirstValue("user_metadata") is { } metadata)
        {
            try
            {
                using var doc = JsonDocument.Parse(metadata);
                foreach (var key in new[] { "full_name", "name" })
                    if (name is null && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        name = v.GetString();
            }
            catch (JsonException) { }
        }
        name ??= user.FindFirstValue("email")?.Split('@')[0] ?? user.FindFirstValue("name") ?? "Player";
        return name.Length > 120 ? name[..120] : name;
    }
}

// Supabase publishes a JWKS but no OpenID discovery document, so build the configuration from the JWKS alone.
internal sealed class JwksConfigurationManager(string jwksUrl) : IConfigurationManager<OpenIdConnectConfiguration>
{
    private static readonly HttpClient Http = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private OpenIdConnectConfiguration? _config;
    private DateTime _expiresAt;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        if (_config is not null && DateTime.UtcNow < _expiresAt) return _config;

        await _lock.WaitAsync(cancel);
        try
        {
            if (_config is null || DateTime.UtcNow >= _expiresAt)
            {
                var jwks = new JsonWebKeySet(await Http.GetStringAsync(jwksUrl, cancel));
                var config = new OpenIdConnectConfiguration();
                foreach (var key in jwks.GetSigningKeys()) config.SigningKeys.Add(key);
                _config = config;
                _expiresAt = DateTime.UtcNow.AddHours(1);
            }
            return _config;
        }
        finally { _lock.Release(); }
    }

    // Called by JwtBearer when a token's key id is unknown (key rotation).
    public void RequestRefresh() => _expiresAt = DateTime.MinValue;
}

internal sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Guid.TryParse(Request.Headers[AuthSetup.DevUserHeader], out var g) ? g : Seed.DevUserId;
        var identity = new ClaimsIdentity([new Claim("sub", id.ToString()), new Claim("name", $"Dev {id.ToString()[^4..]}")],
            AuthSetup.DevScheme, "sub", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.DevScheme)));
    }
}
