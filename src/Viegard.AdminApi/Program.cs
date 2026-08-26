using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Viegard.AdminApi.Auth;
using Viegard.AdminApi.Components;
using Viegard.AdminApi.Configuration;
using Viegard.Application.Audit;
using Viegard.Application.Auth;
using Viegard.Application.Queues;
using Viegard.Application.Secrets;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;
using Viegard.Persistence.Postgres;

var builder = WebApplication.CreateBuilder(args);

var exposure = builder.Configuration.GetSection(AdminExposureOptions.SectionName).Get<AdminExposureOptions>() ?? new();
var exposureMode = exposure.Exposure.ToLowerInvariant();
X509Certificate2? directCertificate = null;
if (exposureMode == AdminExposureModes.Direct)
{
    if (string.IsNullOrWhiteSpace(exposure.Tls.CertificatePath)
        || string.IsNullOrWhiteSpace(exposure.Tls.KeyPath)
        || !File.Exists(exposure.Tls.CertificatePath)
        || !File.Exists(exposure.Tls.KeyPath))
    {
        throw new InvalidOperationException(
            "Viegard admin direct exposure requires readable Viegard:Admin:Tls:CertificatePath and KeyPath PEM files.");
    }

    directCertificate = X509Certificate2.CreateFromPemFile(exposure.Tls.CertificatePath, exposure.Tls.KeyPath);
}

if (exposureMode == AdminExposureModes.Proxy && exposure.Proxy.TrustedNetworks.Length == 0)
{
    throw new InvalidOperationException(
        "Viegard admin proxy exposure requires at least one Viegard:Admin:Proxy:TrustedNetworks entry.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1024 * 1024;
    options.Limits.MaxRequestHeaderCount = 64;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(75);

    if (directCertificate is not null)
    {
        options.ListenAnyIP(exposure.Tls.Port, listen => listen.UseHttps(directCertificate));
    }
});

builder.Services
    .AddOptions<AdminExposureOptions>()
    .Bind(builder.Configuration.GetSection(AdminExposureOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AdminExposureOptions>, AdminExposureOptionsValidator>();

builder.Services
    .AddOptions<AdminAllowedSourcesOptions>()
    .Bind(builder.Configuration.GetSection(AdminAllowedSourcesOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AdminAllowedSourcesOptions>, AdminAllowedSourcesOptionsValidator>();
builder.Services.AddSingleton(sp =>
    new AdminAllowedSourceGate(sp.GetRequiredService<IOptions<AdminAllowedSourcesOptions>>().Value.AllowedSources));

builder.Services
    .AddOptions<AdminAuthOptions>()
    .Bind(builder.Configuration.GetSection(AdminAuthOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AdminAuthOptions>, AdminAuthOptionsValidator>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<RecoveryCodeService>();
builder.Services.AddSingleton<AdminPasswordService>();
builder.Services.AddSingleton<PendingTwoFactorCookie>();
builder.Services.AddSingleton<RecoveryCodesCookie>();
builder.Services.AddSingleton<AdminAuthAuditor>();
builder.Services.AddScoped<AdminCookieAuthenticationEvents>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = AdminCookieNames.Session;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.EventsType = typeof(AdminCookieAuthenticationEvents);
        options.SlidingExpiration = false;
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("admin-global", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(GetClientPartitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 2,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));
});

if (exposureMode == AdminExposureModes.Proxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var trustedNetwork in exposure.Proxy.TrustedNetworks)
        {
            options.KnownIPNetworks.Add(AdminProxyNetworkParser.Parse(trustedNetwork));
        }
    });
}

var secretProviderKind = builder.Configuration["Viegard:Secrets:Provider"] ?? "configuration";
switch (secretProviderKind)
{
    case "file":
        var secretsDirectory = builder.Configuration["Viegard:Secrets:Directory"]
            ?? throw new InvalidOperationException(
                "Viegard:Secrets:Directory must be configured when Viegard:Secrets:Provider is 'file'.");
        builder.Services.AddSingleton<ISecretProvider>(new FileSecretProvider(secretsDirectory));
        break;
    case "configuration":
        builder.Services.AddSingleton<ISecretProvider>(sp => new ConfigurationSecretProvider(builder.Configuration));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown secret provider '{secretProviderKind}'.  Supported: file, configuration.");
}

var persistenceProvider = builder.Configuration["Viegard:Persistence:Provider"] ?? "inmemory";
switch (persistenceProvider)
{
    case "postgres":
        builder.Services.AddViegardPostgresPersistence(builder.Configuration);
        break;

    case "inmemory":
        builder.Services.AddSingleton<IRawObservationStore, InMemoryRawObservationStore>();
        builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
        builder.Services.AddSingleton<IIncidentStore, InMemoryIncidentStore>();
        builder.Services.AddSingleton<IClassificationStore, InMemoryClassificationStore>();
        builder.Services.AddSingleton<IDecisionStore, InMemoryDecisionStore>();
        builder.Services.AddSingleton<IActionStore, InMemoryActionStore>();
        builder.Services.AddSingleton<ICorrectionStore, InMemoryCorrectionStore>();
        builder.Services.AddSingleton<IAdminUserStore, InMemoryAdminUserStore>();
        builder.Services.AddSingleton<IAdminSessionStore, InMemoryAdminSessionStore>();
        builder.Services.AddSingleton<IAuditLedger, InMemoryAuditLedger>();
        builder.Services.AddSingleton<IQueueTelemetryStore, InMemoryQueueTelemetryStore>();
        builder.Services.AddSingleton<ISourceOffsetStore, InMemorySourceOffsetStore>();

        var eventsQueue = new ChannelWorkQueue<Guid>("events");
        var incidentsQueue = new ChannelWorkQueue<IncidentWorkItem>("incidents");
        var classificationsQueue = new ChannelWorkQueue<ClassificationWorkItem>("classifications");
        builder.Services.AddSingleton<IWorkQueue<Guid>>(eventsQueue);
        builder.Services.AddSingleton<IQueueStatsSource>(eventsQueue);
        builder.Services.AddSingleton<IWorkQueue<IncidentWorkItem>>(incidentsQueue);
        builder.Services.AddSingleton<IQueueStatsSource>(incidentsQueue);
        builder.Services.AddSingleton<IWorkQueue<ClassificationWorkItem>>(classificationsQueue);
        builder.Services.AddSingleton<IQueueStatsSource>(classificationsQueue);
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown persistence provider '{persistenceProvider}'.  Supported: inmemory, postgres.");
}

var app = builder.Build();

if (persistenceProvider == "postgres")
{
    await app.Services.MigrateViegardDatabaseAsync().ConfigureAwait(false);
}

await AdminBootstrapper.RunAsync(app.Services).ConfigureAwait(false);

if (exposureMode == AdminExposureModes.Proxy)
{
    app.UseForwardedHeaders();
}

app.Use(async (context, next) =>
{
    // No client-side script ships at all (static SSR, plain form posts),
    // so script-src needs no inline allowance.  The template stylesheet
    // still uses inline styles and data: images.
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context).ConfigureAwait(false);
});

app.Use(async (context, next) =>
{
    var gate = context.RequestServices.GetRequiredService<AdminAllowedSourceGate>();
    if (!gate.IsAllowed(context.Connection.RemoteIpAddress))
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Viegard.AdminApi.AllowedSources");
        logger.LogWarning(
            "Rejected admin request from non-allowed source {RemoteAddress}.",
            Viegard.Application.Logging.LogSanitizer.Sanitize(context.Connection.RemoteIpAddress?.ToString()));
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context).ConfigureAwait(false);
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

if (exposureMode != AdminExposureModes.Loopback)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// Loopback mode assumes the operator does not publish this port beyond the
// local host.  Public or LAN exposure must use direct TLS or proxy mode.

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    if (!authenticated && !IsAnonymousAllowedPath(path))
    {
        await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        return;
    }

    if (authenticated
        && !path.StartsWithSegments("/account")
        && !path.StartsWithSegments("/auth")
        && !path.StartsWithSegments("/login")
        && !path.StartsWithSegments("/healthz")
        && !path.StartsWithSegments("/_framework")
        && !path.StartsWithSegments("/app.css")
        && !path.StartsWithSegments("/Viegard.AdminApi.styles.css"))
    {
        var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            var users = context.RequestServices.GetRequiredService<IAdminUserStore>();
            var user = await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false);
            if (user is { MustChangePassword: true } or { TotpEnrolled: false })
            {
                context.Response.Redirect("/account");
                return;
            }
        }
    }

    await next(context).ConfigureAwait(false);
});
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", service = "viegard-admin" }))
    .AllowAnonymous();
app.MapAdminAuthEndpoints();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>();

app.Run();

static string GetClientPartitionKey(HttpContext context) =>
    context.Connection.RemoteIpAddress is { } remote
        ? Viegard.Application.Auth.AdminIpBinding.Normalize(remote).ToString()
        : "unknown";

static bool IsAnonymousAllowedPath(PathString path) =>
    path.StartsWithSegments("/login")
    || path.StartsWithSegments("/healthz")
    || path.StartsWithSegments("/_framework")
    || path.StartsWithSegments("/_content")
    || path.StartsWithSegments("/app.css")
    || path.StartsWithSegments("/Viegard.AdminApi.styles.css")
    || path.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/auth/totp", StringComparison.OrdinalIgnoreCase);
