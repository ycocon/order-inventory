using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Retail.Payments;

internal sealed record SimulatorResult(string? Decision, string? Error, bool NeedsAttention = false);

internal sealed class PaymentSimulatorClient(HttpClient http)
{
    public async Task<SimulatorResult> ResolveAsync(ClaimedPayment payment, CancellationToken ct)
    {
        var path = $"api/authorizations/{payment.OperationId}";
        try
        {
            if (payment.ReconcileFirst)
            {
                using var lookup = await http.GetAsync(path, ct);
                if (lookup.StatusCode != HttpStatusCode.NotFound)
                    return await ReadAsync(lookup, payment, ct);
            }

            using var response = await http.PutAsJsonAsync(path, new { payment.AmountMinor, payment.Currency }, ct);
            return await ReadAsync(response, payment, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, "Payment simulator call timed out.");
        }
        catch (HttpRequestException)
        {
            return new(null, "Payment simulator could not be reached or its reply was interrupted.");
        }
        catch (JsonException)
        {
            return new(null, "Payment simulator replied with invalid JSON.", NeedsAttention: true);
        }
    }

    private static async Task<SimulatorResult> ReadAsync(HttpResponseMessage response, ClaimedPayment payment, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            return new(null, "Payment simulator refused the stored payment terms.", NeedsAttention: true);
        if (!response.IsSuccessStatusCode)
            return new(null, $"Payment simulator HTTP status {(int)response.StatusCode}.");

        var receipt = await response.Content.ReadFromJsonAsync<SimulatorReceipt>(cancellationToken: ct);
        if (receipt is null || receipt.OperationId != payment.OperationId || receipt.AmountMinor != payment.AmountMinor
            || receipt.Currency != payment.Currency || receipt.Decision is not ("approved" or "declined"))
            return new(null, "Payment simulator reply did not match the requested payment.", NeedsAttention: true);
        return new(receipt.Decision, null);
    }

    private sealed record SimulatorReceipt(Guid OperationId, long? AmountMinor, string? Currency, string? Decision);
}
