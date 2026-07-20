using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Valenius.Backend.Data;
using Valenius.Backend.Filters;
using Valenius.Backend.Licensing;
using Valenius.Backend.Services;
using Valenius.Shared;

namespace Valenius.Backend;

public static class OssStartup
{
    /// <summary>
    /// Registers all OSS services.  Pro project calls this first, then overrides
    /// the four pro-feature interfaces before calling builder.Build().
    /// </summary>
    public static void AddOssServices(this WebApplicationBuilder builder)
    {
        // --- Database ---
        builder.Services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

        // --- Data Protection (persist keys so OIDC state survives restarts) ---
        var dpKeysPath = builder.Configuration["DataProtection:KeysPath"]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ValeniusBackend", "DataProtection");
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
            .SetApplicationName("Valenius");

        // --- Caching (used by rate limiter, TOTP replay protection, etc.) ---
        builder.Services.AddMemoryCache();

        // --- Per-IP rate limiter for login (5 attempts per minute) ---
        builder.Services.AddRateLimiter(opts =>
        {
            opts.AddPolicy("login", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window      = TimeSpan.FromMinutes(1),
                        QueueLimit  = 0,
                    }));
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        // --- HTTP client factory ---
        builder.Services.AddHttpClient();

        // --- Controllers ---
        builder.Services.AddControllers();
        builder.Services.AddScoped<ApiKeyFilter>();
        builder.Services.AddSingleton<ApiKeyStore>();
        builder.Services.AddSingleton<ClientNotifier>();
        builder.Services.AddSingleton<ClientPresenceTracker>();
        builder.Services.AddSingleton<WgTunnelTracker>();

        // Audit log
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IAuditService, AuditService>();

        // E-mail (OSS — real implementation, uses SMTP settings stored in ServerSettings)
        builder.Services.AddSingleton<IEmailService, EmailService>();

        // Pro feature services — no-ops / community defaults in the OSS build.
        // The Pro project calls builder.Services.Replace(...) to substitute real implementations.
        builder.Services.AddSingleton<ICaService, NullCaService>();
        builder.Services.AddSingleton<ISidecarService, NullSidecarService>();
        builder.Services.AddScoped<IPeerProvisioningService, NullPeerProvisioningService>();
        builder.Services.AddScoped<IMfaSessionService, NullMfaSessionService>();
        builder.Services.AddScoped<IMfaChallengeService, NullMfaChallengeService>();
        builder.Services.AddSingleton<ILicenseContext, CommunityLicenseContext>();
        builder.Services.AddScoped<ILicenseEnforcement, CommunityLicenseEnforcement>();
        builder.Services.AddSingleton<IInstallationLicenseMonitor, NullInstallationLicenseMonitor>();

        // --- Razor Pages (admin UI) ---
        builder.Services.AddRazorPages(opts =>
        {
            opts.Conventions.AuthorizeFolder("/Admin",           "AnyRole");
            opts.Conventions.AuthorizeFolder("/Admin/Customers", "AdminOnly");
            opts.Conventions.AuthorizeFolder("/Admin/Users",     "AdminOnly");
            opts.Conventions.AuthorizePage("/Admin/ErrorLog",    "AdminOnly");
            opts.Conventions.AuthorizePage("/Admin/AuditLog",    "AdminOnly");
            opts.Conventions.AuthorizePage("/Admin/Settings",    "AdminOnly");
            opts.Conventions.AuthorizePage("/Admin/Releases",    "AdminOnly");
            opts.Conventions.AuthorizePage("/Admin/Backup",        "AdminOnly");
            opts.Conventions.AuthorizePage("/Admin/Alerts",      "AdminOnly");
            opts.Conventions.AuthorizeFolder("/Admin/Server",    "AdminOnly");
            opts.Conventions.AuthorizeFolder("/Admin/Appliances","AdminOnly");
        });

        // --- Read OIDC configuration from DB before service registration ---
        string? oidcAuthority = null, oidcClientId = null, oidcClientSecret = null, oidcRedirectUri = null;

        if (!builder.Environment.IsDevelopment())
        {
            try
            {
                var tempOpts = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
                    .Options;
                using var tempDb = new ApplicationDbContext(tempOpts);
                var s = tempDb.ServerSettings.OrderBy(x => x.Id).FirstOrDefault();
                if (s is not null)
                {
                    oidcAuthority    = s.OidcAuthority;
                    oidcClientId     = s.OidcClientId;
                    oidcClientSecret = s.OidcClientSecret;
                    oidcRedirectUri  = s.OidcRedirectUri;
                }
            }
            catch { /* DB not ready yet (first run) — OIDC unconfigured, use local auth */ }

            if (string.IsNullOrEmpty(oidcAuthority))
            {
                var legacy = builder.Configuration.GetSection("Authentik");
                oidcAuthority    = legacy["Authority"];
                oidcClientId     = legacy["ClientId"];
                oidcClientSecret = legacy["ClientSecret"];
                oidcRedirectUri  = legacy["RedirectUri"];
            }
        }

        var oidcConfigured = !builder.Environment.IsDevelopment()
                          && !string.IsNullOrEmpty(oidcAuthority)
                          && !string.IsNullOrEmpty(oidcClientId)
                          && !string.IsNullOrEmpty(oidcClientSecret);

        // --- Authentication ---
        if (builder.Environment.IsDevelopment())
        {
            builder.Services
                .AddAuthentication("DevAuth")
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("DevAuth", null);
        }
        else
        {
            var authBuilder = builder.Services
                .AddAuthentication(opts =>
                {
                    opts.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
                    opts.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                })
                .AddCookie(opts =>
                {
                    opts.LoginPath             = "/login";
                    // SameAsRequest, not Always: a fresh install is reachable over plain HTTP
                    // (no reverse proxy yet) for local testing per the self-hosting quick-start
                    // (install.sh / documentation/self-hosting/community.md). Always marks the
                    // cookie Secure unconditionally, so the browser drops it entirely, the redirect
                    // to /Admin/ comes back unauthenticated, and login silently loops back to
                    // /login even with the correct password. UseForwardedHeaders runs before
                    // UseAuthentication (below), so behind the documented TLS-terminating reverse
                    // proxy this still evaluates as HTTPS via X-Forwarded-Proto and stays Secure.
                    opts.Cookie.SecurePolicy   = CookieSecurePolicy.SameAsRequest;
                    // Lax, not Strict: after external (OIDC) login the /signin-oidc callback sets
                    // this cookie and redirects to /Admin/. That navigation chain originated
                    // cross-site (at the IdP), so a Strict cookie is NOT sent on the /Admin/ request
                    // and the user bounces back to /login in a loop (while a manually-typed /admin,
                    // a same-site navigation, works). Lax still blocks CSRF on cross-site POSTs and
                    // subresource loads but is sent on top-level GET navigations like this redirect.
                    opts.Cookie.SameSite       = SameSiteMode.Lax;
                });

            if (oidcConfigured)
            {
                authBuilder.AddOpenIdConnect(opts =>
                {
                    opts.Authority    = oidcAuthority!;
                    opts.ClientId     = oidcClientId!;
                    opts.ClientSecret = oidcClientSecret!;
                    opts.ResponseType         = OpenIdConnectResponseType.Code;
                    opts.SaveTokens           = false;
                    opts.SignedOutRedirectUri = "/login";
                    opts.Scope.Add("openid");
                    opts.Scope.Add("profile");
                    opts.Scope.Add("email");

                    // The correlation + nonce cookies must survive the cross-site round-trip to
                    // the IdP and back. SameSite=None lets the browser send them on the callback;
                    // Secure=Always marks them Secure even if the app can't see that the request is
                    // HTTPS behind the reverse proxy (a missing/untrusted X-Forwarded-Proto would
                    // otherwise leave a None cookie non-Secure, which browsers DROP — the callback
                    // then fails with "Correlation failed"). The site is always served over HTTPS.
                    opts.CorrelationCookie.SameSite     = SameSiteMode.None;
                    opts.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                    opts.NonceCookie.SameSite           = SameSiteMode.None;
                    opts.NonceCookie.SecurePolicy       = CookieSecurePolicy.Always;

                    opts.Events = new OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = ctx =>
                        {
                            if (!string.IsNullOrEmpty(oidcRedirectUri))
                            {
                                ctx.ProtocolMessage.RedirectUri  = oidcRedirectUri;
                                ctx.ProtocolMessage.ResponseMode = "query";
                            }
                            return Task.CompletedTask;
                        },
                        OnAuthorizationCodeReceived = ctx =>
                        {
                            if (!string.IsNullOrEmpty(oidcRedirectUri))
                                ctx.TokenEndpointRequest!.RedirectUri = oidcRedirectUri;
                            return Task.CompletedTask;
                        },
                        OnTokenValidated = ctx =>
                        {
                            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                "sub",
                                System.Security.Claims.ClaimTypes.NameIdentifier,
                                "name",
                                System.Security.Claims.ClaimTypes.Name,
                                "email",
                                System.Security.Claims.ClaimTypes.Email,
                            };
                            var identity = (System.Security.Claims.ClaimsIdentity)ctx.Principal!.Identity!;
                            foreach (var c in identity.Claims.Where(c => !keep.Contains(c.Type)).ToList())
                                identity.RemoveClaim(c);
                            return Task.CompletedTask;
                        },
                        OnTicketReceived = ctx =>
                        {
                            if (string.IsNullOrEmpty(ctx.ReturnUri))
                                ctx.ReturnUri = "/Admin/";
                            return Task.CompletedTask;
                        },
                        OnRemoteFailure = ctx =>
                        {
                            ctx.HandleResponse();
                            var msg = Uri.EscapeDataString(ctx.Failure?.Message ?? "Authentication failed");
                            ctx.Response.Redirect($"/autherror?msg={msg}");
                            return Task.CompletedTask;
                        }
                    };
                });
            }
        }

        builder.Services.AddAuthorization(opts =>
        {
            opts.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
            opts.AddPolicy("AnyRole",   p => p.RequireRole("Admin", "User"));
        });
        builder.Services.AddScoped<IClaimsTransformation, AppUserClaimsTransformation>();
    }

    /// <summary>
    /// Runs DB schema migrations, seeds required rows, and bootstraps the CA.
    /// Called after builder.Build(), before the middleware pipeline.
    /// </summary>
    public static async Task InitialiseOssAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // All tables are EF-managed; EnsureCreated creates the full current schema on fresh installs.
        db.Database.EnsureCreated();

        // Schema patches run immediately after EnsureCreated — before any EF query that SELECTs
        // these columns. EF includes every mapped property in its SELECT list, so a new column must
        // exist in the DB before the first FirstAsync / Any call on that table.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "AppUsers" ADD COLUMN IF NOT EXISTS "TotpLastUsedInterval" BIGINT NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "ApiKey" VARCHAR(128) NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "PreviousApiKey" VARCHAR(128) NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "ApiKeyRotatedAt" TIMESTAMP NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "TrafficRetentionDays" INTEGER NOT NULL DEFAULT 90""");
        // WireGuard traffic accounting: reset-aware lifetime counters on the client + a per-interval sample table.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "WgRxLastRaw" BIGINT NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "WgTxLastRaw" BIGINT NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "WgRxLifetime" BIGINT NOT NULL DEFAULT 0""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "WgTxLifetime" BIGINT NOT NULL DEFAULT 0""");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ClientTrafficSamples" (
                "Id"        BIGSERIAL PRIMARY KEY,
                "ClientId"  INTEGER   NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "SampledAt" TIMESTAMP NOT NULL,
                "RxBytes"   BIGINT    NOT NULL,
                "TxBytes"   BIGINT    NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ClientTrafficSamples_Client_SampledAt" ON "ClientTrafficSamples" ("ClientId", "SampledAt")""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "LicenseLastValidatedAt" TIMESTAMP NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ServerSettings" ADD COLUMN IF NOT EXISTS "LicenseStatus" VARCHAR(20) NOT NULL DEFAULT 'Valid'""");

        // MFA session gating (Pro) — columns inert in OSS (NullMfaSessionService). See
        // docs/design/mfa-session-gating.md. Existing peers default to User/Disabled so
        // nothing auto-enables enforcement.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "PeerType" VARCHAR(10) NOT NULL DEFAULT 'User'""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaPolicy" INT NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaSessionLifetimeHours" INT NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaTotpSecret" VARCHAR(64) NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaTotpEnabled" BOOLEAN NOT NULL DEFAULT FALSE""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaEnrollmentOpen" BOOLEAN NOT NULL DEFAULT FALSE""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaTotpLastUsedInterval" BIGINT NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "IsDuplicatePending" BOOLEAN NOT NULL DEFAULT FALSE""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "DefaultMfaPolicy" INT NOT NULL DEFAULT 0""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "MfaSessionLifetimeHours" INT NOT NULL DEFAULT 12""");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "MfaSessions" (
                "Id"            SERIAL PRIMARY KEY,
                "ClientId"      INT NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "CustomerId"    INT NULL,
                "SessionId"     UUID NOT NULL,
                "Nonce"         VARCHAR(64) NOT NULL DEFAULT '',
                "IssuedAt"      TIMESTAMP NOT NULL,
                "ExpiresAt"     TIMESTAMP NOT NULL,
                "Status"        VARCHAR(10) NOT NULL DEFAULT 'Active',
                "CreatedFromIp" VARCHAR(45) NULL,
                "RevokedAt"     TIMESTAMP NULL,
                "RevokedBy"     VARCHAR(200) NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_MfaSessions_SessionId" ON "MfaSessions" ("SessionId")""");
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_MfaSessions_ClientId_Status" ON "MfaSessions" ("ClientId", "Status")""");

        // Cross-customer sidecar sharing (Pro) — inert in OSS.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "AllowShareSidecar" BOOLEAN NOT NULL DEFAULT FALSE""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "AllowReceiveForeignProfiles" BOOLEAN NOT NULL DEFAULT FALSE""");

        // DNS search domains — appended to the DNS line as NRPT split-DNS rules so multiple
        // simultaneous tunnels can each own their own DNS without colliding.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "DnsSearchDomains" VARCHAR(255) NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "DnsSearchDomains" VARCHAR(255) NULL""");

        // Client platform/OS ("Windows"/"Linux"/"Android"/"iOS") so the admin UI can
        // identify mobile devices and the backend can skip desktop-only assumptions.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "Platform" VARCHAR(20) NULL""");

        // Admin-set friendly name shown in place of the hostname when present.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "DisplayName" VARCHAR(255) NULL""");

        // FCM token for push-to-approve MFA (mobile).
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "FcmToken" VARCHAR(255) NULL""");

        // Per-client push-to-approve approver device.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "MfaApproverClientId" INT NULL""");

        // Cross-device push-to-approve MFA challenges.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "MfaChallenges" (
                "Id"                SERIAL PRIMARY KEY,
                "RequesterClientId" INT NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "ApproverClientId"  INT NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "MatchNumber"       INT NOT NULL,
                "Choices"           VARCHAR(32) NOT NULL DEFAULT '',
                "Status"            VARCHAR(20) NOT NULL DEFAULT 'Pending',
                "RequesterIp"       VARCHAR(45) NULL,
                "CreatedAt"         TIMESTAMP NOT NULL,
                "ExpiresAt"         TIMESTAMP NOT NULL
            )
            """);

        // QR pairing codes — a device scans one to activate + join a customer in one step.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "PairingTokens" (
                "Id"         SERIAL PRIMARY KEY,
                "Token"      VARCHAR(32) NOT NULL,
                "CustomerId" INT NOT NULL REFERENCES "Customers"("Id") ON DELETE CASCADE,
                "CreatedAt"  TIMESTAMP NOT NULL,
                "ExpiresAt"  TIMESTAMP NOT NULL,
                "UsedAt"     TIMESTAMP NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PairingTokens_Token" ON "PairingTokens"("Token")""");

        // Sidecar transit-network CIDR — must be globally disjoint so sidecar gateway IPs are
        // unique, which makes the gateway /health verification safe across multiple tunnels.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "SidecarVpnSubnet" VARCHAR(45) NULL""");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ClientForeignProfiles" (
                "Id"                    SERIAL PRIMARY KEY,
                "ClientId"              INT NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "SourceCustomerId"      INT NOT NULL REFERENCES "Customers"("Id") ON DELETE CASCADE,
                "WgPublicKey"           VARCHAR(50) NULL,
                "WgPrivateKey"          VARCHAR(50) NULL,
                "VpnIpAddress"          VARCHAR(45) NULL,
                "PendingConfigFileName" VARCHAR(260) NULL,
                "PendingConfigContent"  TEXT NULL,
                "ConfigProvisionedAt"   TIMESTAMP NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClientForeignProfiles_ClientId_SourceCustomerId" ON "ClientForeignProfiles" ("ClientId", "SourceCustomerId")""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientForeignProfiles" ADD COLUMN IF NOT EXISTS "ProfileName" VARCHAR(50) NULL""");

        // Set by the admin "Kill Tunnel" action; cleared + peer re-added on the client's next Connect event.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "WgPeerKilled" BOOLEAN NOT NULL DEFAULT FALSE""");

        // Retained copy of every config delivered to a client (keyed by client + profile
        // name) so a reinstall reassign can re-send all profiles — see ClientConfigArchive.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ClientConfigArchives" (
                "Id"            SERIAL PRIMARY KEY,
                "ClientId"      INT NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "ProfileName"   VARCHAR(50) NOT NULL,
                "Content"       TEXT NOT NULL,
                "PendingResend" BOOLEAN NOT NULL DEFAULT FALSE,
                "UpdatedAt"     TIMESTAMP NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc')
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ClientConfigArchives_ClientId_ProfileName" ON "ClientConfigArchives" ("ClientId", "ProfileName")""");

        // Diagnostics: one-shot admin "Request logs" flag + uploaded (gzipped, redacted) log bundles.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "PendingLogRequest" BOOLEAN NOT NULL DEFAULT FALSE""");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ClientLogBundles" (
                "Id"          SERIAL PRIMARY KEY,
                "ClientId"    INT NOT NULL REFERENCES "Clients"("Id") ON DELETE CASCADE,
                "UploadedAt"  TIMESTAMP NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc'),
                "Trigger"     VARCHAR(20) NOT NULL DEFAULT 'Admin',
                "FileName"    VARCHAR(260) NOT NULL DEFAULT '',
                "ContentType" VARCHAR(100) NOT NULL DEFAULT 'application/gzip',
                "SizeBytes"   INT NOT NULL DEFAULT 0,
                "Content"     BYTEA NOT NULL DEFAULT ''::bytea
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ClientLogBundles_ClientId_UploadedAt" ON "ClientLogBundles" ("ClientId", "UploadedAt")""");

        // Per-OS client release streams (windows / linux / macos / android). Replaces the single
        // shared versions.json manifest — see Admin/Releases + GET /api/version{,/os}.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ClientReleases" (
                "Id"           SERIAL PRIMARY KEY,
                "Os"           VARCHAR(20) NOT NULL,
                "Version"      VARCHAR(40) NOT NULL,
                "FileName"     VARCHAR(260) NOT NULL,
                "Sha256"       VARCHAR(64) NOT NULL DEFAULT '',
                "SizeBytes"    BIGINT NOT NULL DEFAULT 0,
                "ReleaseNotes" TEXT NOT NULL DEFAULT '',
                "UploadedAt"   TIMESTAMP NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc'),
                "IsPublished"  BOOLEAN NOT NULL DEFAULT FALSE
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_ClientReleases_Os_UploadedAt" ON "ClientReleases" ("Os", "UploadedAt")""");
        // Beta channel: per (Os, Channel) publishing + per-client channel selection.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "ClientReleases" ADD COLUMN IF NOT EXISTS "Channel" VARCHAR(10) NOT NULL DEFAULT 'stable'""");
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Clients" ADD COLUMN IF NOT EXISTS "UpdateChannel" VARCHAR(10) NOT NULL DEFAULT 'stable'""");
        // Sidecar-reported physical LAN CIDR(s) behind the host, cached from /info — lets clients
        // detect a pre-connect LAN conflict even for full-tunnel (0.0.0.0/0) profiles.
        await db.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "SidecarLanCidrs" VARCHAR(500) NULL""");

        // Seed ServerSettings row (Id = 1).
        if (!db.ServerSettings.Any())
        {
            db.ServerSettings.Add(new Models.ServerSettings());
            db.SaveChanges();
        }

        // One-time migration: fold the legacy single versions.json into per-OS ClientReleases so
        // the live fleet keeps getting GET /api/version after this deploy. Runs only when the new
        // table is empty; failures are non-fatal (an admin can re-publish from Admin/Releases).
        if (!await db.ClientReleases.AnyAsync())
        {
            try
            {
                var rawM = app.Configuration["Valenius:ManifestPath"]  ?? "versions.json";
                var rawD = app.Configuration["Valenius:DownloadsPath"] ?? "downloads";
                var mPath = Path.IsPathRooted(rawM) ? rawM : Path.Combine(AppContext.BaseDirectory, rawM);
                var dPath = Path.IsPathRooted(rawD) ? rawD : Path.Combine(AppContext.BaseDirectory, rawD);

                if (File.Exists(mPath))
                {
                    var manifest = System.Text.Json.JsonSerializer.Deserialize(
                        await File.ReadAllTextAsync(mPath), Valenius.Shared.ValeniusJsonContext.Default.VersionManifest);
                    if (manifest is not null)
                    {
                        long SizeOf(string? url)
                        {
                            var fn = FileNameFromUrl(url);
                            var p  = fn is null ? null : Path.Combine(dPath, fn);
                            return p is not null && File.Exists(p) ? new FileInfo(p).Length : 0;
                        }

                        var winFile = FileNameFromUrl(manifest.DownloadUrl);
                        if (!string.IsNullOrEmpty(manifest.Version) && winFile is not null)
                            db.ClientReleases.Add(new Models.ClientRelease
                            {
                                Os = Models.ReleaseOs.Windows, Version = manifest.Version, FileName = winFile,
                                Sha256 = manifest.Sha256, SizeBytes = SizeOf(manifest.DownloadUrl),
                                ReleaseNotes = manifest.ReleaseNotes, UploadedAt = DateTime.UtcNow, IsPublished = true,
                            });

                        var linuxFile = FileNameFromUrl(manifest.LinuxUrl);
                        if (!string.IsNullOrEmpty(manifest.LinuxUrl) && linuxFile is not null)
                            db.ClientReleases.Add(new Models.ClientRelease
                            {
                                Os = Models.ReleaseOs.Linux, Version = manifest.Version, FileName = linuxFile,
                                Sha256 = manifest.LinuxSha256 ?? "", SizeBytes = SizeOf(manifest.LinuxUrl),
                                ReleaseNotes = manifest.ReleaseNotes, UploadedAt = DateTime.UtcNow, IsPublished = true,
                            });

                        await db.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "versions.json -> ClientReleases migration skipped.");
            }
        }

        // One-time migration: copy OIDC settings from appsettings/env-vars into DB.
        {
            var s = await db.ServerSettings.FirstAsync();
            if (string.IsNullOrEmpty(s.OidcAuthority))
            {
                var legacy = app.Configuration.GetSection("Authentik");
                s.OidcAuthority    = legacy["Authority"];
                s.OidcClientId     = legacy["ClientId"];
                s.OidcClientSecret = legacy["ClientSecret"];
                s.OidcRedirectUri  = legacy["RedirectUri"];
                if (!string.IsNullOrEmpty(s.OidcAuthority))
                    s.OidcEnabled = true;
                await db.SaveChangesAsync();
            }
        }

        // Ensure every install has an API key stored in the DB.
        // Fresh installs: generate a random 256-bit hex key.
        // Existing installs: migrate the key from appsettings.json (one-time).
        {
            var s = await db.ServerSettings.FirstAsync();
            if (string.IsNullOrEmpty(s.ApiKey))
            {
                var fromConfig = app.Configuration["Valenius:ApiKey"];
                s.ApiKey = string.IsNullOrEmpty(fromConfig)
                    ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
                    : fromConfig;
                await db.SaveChangesAsync();
            }
            var store = app.Services.GetRequiredService<ApiKeyStore>();
            store.Key         = s.ApiKey!;
            store.PreviousKey = s.PreviousApiKey;
        }

        // Bootstrap internal CA (no-op in OSS; Pro CaService generates key material).
        var caService = app.Services.GetRequiredService<ICaService>();
        await caService.EnsureBootstrappedAsync();

        // Bootstrap admin user from env vars.
        {
            var adminEmail    = app.Configuration["Valenius:AdminEmail"];
            if (string.IsNullOrEmpty(adminEmail))
                adminEmail    = app.Configuration["ADMIN_EMAIL"];
            var adminPassword = app.Configuration["Valenius:AdminPassword"];
            if (string.IsNullOrEmpty(adminPassword))
                adminPassword = app.Configuration["ADMIN_PASSWORD"];
            if (!string.IsNullOrEmpty(adminEmail) && !string.IsNullOrEmpty(adminPassword))
            {
                const string bootstrapSubject = "local:bootstrap";
                var bootstrap = await db.AppUsers.FirstOrDefaultAsync(u => u.Subject == bootstrapSubject);
                if (bootstrap is null)
                {
                    bootstrap = new Models.AppUser { Subject = bootstrapSubject, Role = "Admin" };
                    db.AppUsers.Add(bootstrap);
                }
                bootstrap.DisplayName = "Admin";
                bootstrap.Email       = adminEmail;
                // Only re-hash when the password actually changed (avoids unnecessary write on every restart).
                // When it DOES change (admin set a new ADMIN_PASSWORD in env vars), also clear TOTP so the
                // admin is never locked out of the bootstrap account after a phone loss.
                if (bootstrap.PasswordHash is null || !PasswordHasher.Verify(adminPassword, bootstrap.PasswordHash))
                {
                    bootstrap.PasswordHash = PasswordHasher.Hash(adminPassword);
                    bootstrap.TotpSecret   = null;
                    bootstrap.TotpEnabled  = false;
                }
                await db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Configures the middleware pipeline and maps all endpoints.
    /// Called after InitialiseOssAsync.
    /// </summary>
    public static void UseOssPipeline(this WebApplication app)
    {
        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
                var ex = feature?.Error;
                if (ex != null)
                {
                    try
                    {
                        using var scope = context.RequestServices.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var fullMessage = ex.InnerException is null
                            ? ex.Message
                            : $"{ex.Message} | Inner: {ex.InnerException.Message}"
                              + (ex.InnerException.InnerException is { } inner2 ? $" | {inner2.Message}" : "");
                        db.ErrorLogs.Add(new Models.ErrorLog
                        {
                            OccurredAt    = DateTime.UtcNow,
                            RequestPath   = feature?.Path,
                            ExceptionType = ex.GetType().FullName,
                            Message       = fullMessage,
                            StackTrace    = ex.StackTrace
                        });
                        await db.SaveChangesAsync();
                    }
                    catch { }
                }

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Internal server error\"}");
                }
                else
                {
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Internal server error.");
                }
            });
        });

        var forwardedOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        // Trust loopback and all Docker bridge/overlay subnets so that X-Forwarded-For from Nginx
        // is honoured regardless of which internal IP the Docker network assigns.
        forwardedOptions.KnownProxies.Add(IPAddress.Loopback);
        forwardedOptions.KnownProxies.Add(IPAddress.IPv6Loopback);
#pragma warning disable ASPDEPR005
        forwardedOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12)); // Docker bridge
        forwardedOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"),   8));  // Docker overlay / custom
#pragma warning restore ASPDEPR005
        app.UseForwardedHeaders(forwardedOptions);

        // Security headers (defence-in-depth for the browser-facing admin UI).
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Frame-Options"]           = "DENY";
            ctx.Response.Headers["X-Content-Type-Options"]    = "nosniff";
            ctx.Response.Headers["Referrer-Policy"]           = "strict-origin-when-cross-origin";
            ctx.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
            await next();
        });

        app.UseHsts();
        app.UseStaticFiles();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapRazorPages();

        // Root -> admin. There is no landing page at "/", so a bare hit on the domain (e.g. an
        // IdP app-launcher tile whose Launch URL is the site root) would go nowhere. Send it to
        // /Admin/, which triggers the normal auth challenge (cookie -> OIDC) and lands the user
        // in the panel.
        app.MapGet("/", () => Results.Redirect("/Admin/"));

        // One-click SSO entry point (GET). Point an IdP app-launcher tile's Launch URL here
        // (e.g. https://your-valenius-host/sso) to start the OIDC challenge directly, so the user
        // lands straight in /Admin without seeing the login page's "Continue with <IdP>" button.
        // Falls back to the normal login page if OIDC isn't available, and short-circuits to the
        // panel if the user already has a session.
        app.MapGet("/sso", async (HttpContext ctx, ApplicationDbContext db, IAuthenticationSchemeProvider schemes) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
                return Results.Redirect("/Admin/");
            var settings = await db.ServerSettings.FirstAsync();
            var oidcAvailable = await schemes.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme) is not null;
            if (!settings.OidcEnabled || !oidcAvailable)
                return Results.Redirect("/login");
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/Admin/" },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        app.MapGet("/autherror", (string? msg) =>
            Results.Text($"Authentication error: {msg}", "text/plain"));

        // --- Version manifest + installer download ---
        var appDir       = AppContext.BaseDirectory;
        var rawDownloads = app.Configuration["Valenius:DownloadsPath"] ?? "downloads";
        var downloadsPath = Path.IsPathRooted(rawDownloads) ? rawDownloads : Path.Combine(appDir, rawDownloads);
        // Baked-into-the-image download assets (e.g. the sidecar host-prep script). Unlike
        // downloadsPath — which lives on a bind-mounted volume and can be wiped/empty — this
        // directory ships inside the image, so these files are always served. Served as a
        // fallback by /api/download/{fileName}. Empty/absent in OSS (its Dockerfile bakes nothing).
        var rawAssets    = app.Configuration["Valenius:DownloadAssetsPath"] ?? "download-assets";
        var assetsPath   = Path.IsPathRooted(rawAssets) ? rawAssets : Path.Combine(appDir, rawAssets);

        static string DownloadUrlFor(string fileName) => $"/api/download/{Uri.EscapeDataString(fileName)}";

        // Legacy combined manifest — kept byte-compatible for the deployed Windows/Linux fleet:
        // Windows fields from the published Windows release, Linux fields from the published Linux one.
        // ?channel=beta selects the beta stream (falling back to stable when no beta exists); no
        // channel param ⇒ stable, so existing clients are unaffected.
        app.MapGet("/api/version", async (string? channel, ApplicationDbContext db) =>
        {
            var ch  = Models.ReleaseChannel.Normalize(channel);
            var win = await ResolvePublishedReleaseAsync(db, Models.ReleaseOs.Windows, ch);
            var lin = await ResolvePublishedReleaseAsync(db, Models.ReleaseOs.Linux, ch);
            if (win is null && lin is null)
                return Results.NotFound("No published release.");
            return Results.Ok(new VersionManifest
            {
                Version      = win?.Version ?? "",
                DownloadUrl  = win is not null ? DownloadUrlFor(win.FileName) : "",
                Sha256       = win?.Sha256 ?? "",
                ReleaseNotes = win?.ReleaseNotes ?? "",
                LinuxUrl     = lin is not null ? DownloadUrlFor(lin.FileName) : null,
                LinuxSha256  = lin?.Sha256,
            });
        });

        // Per-OS stream — windows | linux | macos | android. Independent version per OS + channel.
        // ?channel=beta with beta→stable fallback; no param ⇒ stable.
        app.MapGet("/api/version/{os}", async (string os, string? channel, ApplicationDbContext db) =>
        {
            os = os.ToLowerInvariant();
            if (!Models.ReleaseOs.IsValid(os)) return Results.NotFound("Unknown OS stream.");
            var rel = await ResolvePublishedReleaseAsync(db, os, Models.ReleaseChannel.Normalize(channel));
            if (rel is null) return Results.NotFound($"No published {os} release.");
            return Results.Ok(new VersionManifest
            {
                Version      = rel.Version,
                DownloadUrl  = DownloadUrlFor(rel.FileName),
                Sha256       = rel.Sha256,
                ReleaseNotes = rel.ReleaseNotes,
            });
        });

        app.MapGet("/api/download/{fileName}", (string fileName, HttpContext ctx) =>
        {
            var safeName = Path.GetFileName(fileName);
            // Prefer an admin-uploaded file on the volume; fall back to a baked-in asset.
            var filePath = Path.Combine(downloadsPath, safeName);
            if (!File.Exists(filePath))
                filePath = Path.Combine(assetsPath, safeName);
            if (!File.Exists(filePath))
                return Results.NotFound($"File '{safeName}' not found.");

            // Text assets (host-prep script, deploy compose) carry a {{BACKEND_URL}} placeholder
            // that must resolve to THIS backend's own address, not a fixed example — the same
            // baked-in file is served identically by every Pro deployment (Stranto's own and every
            // MSP customer's), so a hardcoded example URL would be wrong for everyone except one
            // specific installation. Binary downloads (installers, etc.) skip this entirely.
            if (safeName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                || safeName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || safeName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                // Forced to https, not ctx.Request.Scheme: this backend sits behind a reverse
                // proxy that terminates TLS, and Scheme silently falls back to http whenever the
                // proxy's IP falls outside the trusted KnownNetworks for X-Forwarded-Proto (see
                // UseForwardedHeaders above). A sidecar handed http://... as BACKEND_URL gets
                // redirected to https by the proxy, and most HTTP clients (Go's included)
                // downgrade POST to GET on 301/302, turning enrollment into a bare
                // "405 Method Not Allowed" instead of a working POST.
                var backendUrl = $"https://{ctx.Request.Host}";
                var text = File.ReadAllText(filePath).Replace("{{BACKEND_URL}}", backendUrl);
                // Both files use non-ASCII box-drawing/em-dash characters in their header comments.
                // Without an explicit charset, "text/plain" defaults to Latin-1 in most browsers,
                // mangling every UTF-8 multi-byte character into mojibake (e.g. "──" -> "â”€â”€").
                return Results.Text(text, "text/plain", System.Text.Encoding.UTF8);
            }

            var stream = File.OpenRead(filePath);
            return Results.File(stream, "application/octet-stream", safeName);
        });
    }

    /// <summary>The published release for (os, channel). A beta request with no published beta
    /// falls back to the published stable release, so beta clients never fall behind.</summary>
    private static async Task<Models.ClientRelease?> ResolvePublishedReleaseAsync(
        ApplicationDbContext db, string os, string channel)
    {
        var rel = await db.ClientReleases
            .Where(r => r.Os == os && r.Channel == channel && r.IsPublished)
            .OrderByDescending(r => r.UploadedAt)
            .FirstOrDefaultAsync();
        if (rel is null && channel == Models.ReleaseChannel.Beta)
            rel = await db.ClientReleases
                .Where(r => r.Os == os && r.Channel == Models.ReleaseChannel.Stable && r.IsPublished)
                .OrderByDescending(r => r.UploadedAt)
                .FirstOrDefaultAsync();
        return rel;
    }

    /// <summary>Extracts the download file name from a root-relative or absolute download URL
    /// (e.g. "/api/download/ValeniusSetup-0.2.1.exe" → "ValeniusSetup-0.2.1.exe").</summary>
    private static string? FileNameFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var name = Uri.TryCreate(url, UriKind.Absolute, out var abs)
            ? Path.GetFileName(abs.AbsolutePath)
            : Path.GetFileName(url);
        return string.IsNullOrEmpty(name) ? null : Uri.UnescapeDataString(name);
    }
}
