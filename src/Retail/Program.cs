using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Retail.Catalog;
using Retail.Data;
using Retail.Inventory;
using Retail.Messaging;
using Retail.Orders;
using Retail.Payments;

if (args is ["--timeline-relay"] or ["--timeline-reader"])
{
    await TimelineHost.RunAsync(args[0] == "--timeline-relay" ? "relay" : "reader");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRetailDb();
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 8;
}).AddEntityFrameworkStores<RetailDb>();
builder.Services.ConfigureApplicationCookie(cookie =>
{
    cookie.Cookie.Name = "retail.session";
    cookie.Cookie.SameSite = SameSiteMode.Strict;
    cookie.Cookie.HttpOnly = true;
    cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    cookie.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    cookie.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
if (builder.Configuration["Payments:SimulatorUrl"] is { } simulatorUrl)
{
    if (!Uri.TryCreate(simulatorUrl.TrimEnd('/') + "/", UriKind.Absolute, out var simulatorAddress)
        || simulatorAddress.Scheme is not ("http" or "https"))
        throw new InvalidOperationException("Payments:SimulatorUrl must be an HTTP or HTTPS address.");
    builder.Services.AddHttpClient<PaymentSimulatorClient>(client =>
    {
        client.BaseAddress = simulatorAddress;
        client.Timeout = TimeSpan.FromSeconds(3);
    });
    builder.Services.AddScoped<PaymentWorkStore>();
    builder.Services.AddHostedService<PaymentWorker>();
}
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "retail.csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await DemoStartup.InitializeAsync(scope.ServiceProvider);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
    {
        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.BadRequest(new { error = "Refresh your session and submit again." });
    }
    return await next(context);
});
api.MapGet("/session", (HttpContext http, IAntiforgery csrf) =>
{
    http.Response.Headers.CacheControl = "no-store";
    var user = http.User.Identity?.IsAuthenticated == true
        ? new { email = http.User.Identity.Name, role = http.User.IsInRole("staff") ? "staff" : "customer" }
        : null;
    return Results.Ok(new { user, csrfToken = csrf.GetAndStoreTokens(http).RequestToken });
});
api.MapPost("/login", async (LoginInput login, SignInManager<IdentityUser> signIn) =>
{
    if (string.IsNullOrWhiteSpace(login.Email) || string.IsNullOrEmpty(login.Password))
        return Results.BadRequest(new { error = "Enter an email and password." });
    var result = await signIn.PasswordSignInAsync(login.Email.Trim(), login.Password, false, lockoutOnFailure: true);
    return result.Succeeded ? Results.NoContent() : Results.Unauthorized();
});
api.MapPost("/logout", async (SignInManager<IdentityUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();
api.MapCatalog();
api.MapReceipts();
api.MapOrders();
api.MapStaffOrders(builder.Configuration.GetValue<bool>("Messaging:TimelineEnabled"));
app.MapGet("/health/ready", async (RetailDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.Map("/api/{**unmatched}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public sealed record LoginInput(string? Email, string? Password);
public partial class Program { }
