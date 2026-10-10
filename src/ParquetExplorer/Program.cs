using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using ParquetExplorer.Components;
using ParquetExplorer.Endpoints;
using ParquetExplorer.Options;
using ParquetExplorer.Services;

// ---- CLI helpers -------------------------------------------------------------------------
if (args.Length > 0 && args[0] == "hash-password")
{
    var pwd = args.Length > 1 ? args[1] : ReadPassword();
    Console.WriteLine(UserStore.HashPassword(pwd));
    return;
}
if (args.Length > 0 && args[0] == "install-extensions")
{
    DuckDbEngine.InstallExtensions(args.Length > 1 ? args[1] : null);
    Console.WriteLine("DuckDB extensions installed.");
    return;
}

// ---- Web app -----------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExplorerOptions>(builder.Configuration.GetSection(ExplorerOptions.Section));
builder.Services.PostConfigure<ExplorerOptions>(o =>
{
    // Relative local paths are resolved against the content root, not the working directory.
    foreach (var src in o.DataSources.Where(s => s.Type == DataSourceType.Local && !string.IsNullOrWhiteSpace(s.Path)))
        src.Path = Path.GetFullPath(src.Path!, builder.Environment.ContentRootPath);
});
var authOptions = builder.Configuration.GetSection(ExplorerOptions.Section).Get<ExplorerOptions>()?.Auth ?? new AuthOptions();

// Persist cookie encryption keys so sessions survive restarts (mount this folder as a volume in containers).
var keysPath = builder.Configuration[$"{ExplorerOptions.Section}:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("ParquetExplorer");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.AccessDeniedPath = "/login";
        o.Cookie.Name = "px_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(authOptions.SessionHours);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = ctx =>
        {
            // Users removed from configuration lose access immediately.
            var store = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
            if (!store.Exists(ctx.Principal?.Identity?.Name)) ctx.RejectPrincipal();
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                // Plain 401 for API calls: no redirect and no re-executed status code page.
                ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>()?.Enabled = false;
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<DuckDbEngine>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<QueryHistoryService>();
builder.Services.AddSingleton<QueryService>();
builder.Services.AddHostedService<CatalogRefreshService>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

if (authOptions.Users.Count == 0)
    app.Logger.LogWarning("No users configured: add users under ParquetExplorer:Auth:Users (see README).");

// Start DuckDB eagerly so configuration errors surface at start-up.
app.Services.GetRequiredService<DuckDbEngine>();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.MapExportEndpoints();
app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ReadPassword()
{
    Console.Write("Password: ");
    var pwd = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (pwd.Length > 0) pwd.Length--; continue; }
        pwd.Append(key.KeyChar);
    }
    Console.WriteLine();
    return pwd.ToString();
}
