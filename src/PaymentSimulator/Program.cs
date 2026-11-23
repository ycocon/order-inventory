using Npgsql;
using PaymentSimulator;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Simulator")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Simulator.");
var decision = builder.Configuration["Simulator:Decision"] ?? "approved";
if (decision is not ("approved" or "declined"))
    throw new InvalidOperationException("Simulator:Decision must be approved or declined.");
var allowDropReply = builder.Configuration.GetValue<bool>("Simulator:AllowDropReply");
var dropNewReplies = builder.Configuration.GetValue<bool>("Simulator:DropNewReplies");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
builder.Services.AddSingleton(services => new AuthorizationLedger(services.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();

app.MapGet("/health/ready", async (AuthorizationLedger ledger, CancellationToken ct) =>
{
    try
    {
        await ledger.CheckReadyAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

var authorizations = app.MapGroup("/api/authorizations");
authorizations.MapGet("/{operationId:guid}", async (Guid operationId, AuthorizationLedger ledger, CancellationToken ct) =>
{
    var authorization = await ledger.FindAsync(operationId, ct);
    return authorization is null ? Results.NotFound() : Results.Ok(authorization);
});
authorizations.MapPut("/{operationId:guid}", async (Guid operationId, AuthorizationInput input,
    AuthorizationLedger ledger, HttpContext context, CancellationToken ct) =>
{
    var currency = input.Currency?.Trim().ToUpperInvariant();
    if (operationId == Guid.Empty || input.AmountMinor is null or < 0
        || currency is not { Length: 3 } || currency.Any(character => character is < 'A' or > 'Z'))
        return Results.BadRequest(new { error = "Provide a payment ID, a nonnegative amount in minor units, and a three-letter currency." });

    var (authorization, created) = await ledger.AuthorizeAsync(operationId, input.AmountMinor.Value, currency, decision, ct);
    if (authorization.AmountMinor != input.AmountMinor || authorization.Currency != currency)
        return Results.Conflict(new { error = "This payment ID already identifies different payment terms." });

    if (created)
        app.Logger.LogInformation("Simulated payment {OperationId} recorded as {Decision}", operationId, authorization.Decision);

    if (allowDropReply && ((created && dropNewReplies) || context.Request.Headers["X-Simulator-Drop-Reply"] == "true"))
    {
        // The ledger transaction has already committed when this connection is interrupted.
        context.Abort();
        return Results.Empty;
    }

    return created ? Results.Created($"/api/authorizations/{operationId}", authorization) : Results.Ok(authorization);
});

app.Run();

internal sealed record AuthorizationInput(long? AmountMinor, string? Currency);
