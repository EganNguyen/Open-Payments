using OpenPayments.Domain;
using OpenPayments.Transitions;

namespace OpenPayments.Api.Endpoints;

public sealed record InternalApplyRequest(Guid ApproverStaffId);

/// <summary>
/// Task C5: the console's only way to apply a resolution. It is registered only when
/// <c>Internal:Enabled</c> is true and needs a key with the <c>console:resolve</c> scope, so a
/// merchant key can't reach it. The route is deliberately absent from the public contract.
/// </summary>
public static class InternalEndpoints
{
    public static void MapInternalRoutes(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Internal:Enabled", false))
        {
            return;
        }

        app.MapPost("/internal/v1/resolutions/{id:guid}/apply", async (
            Guid id,
            InternalApplyRequest body,
            HttpContext context,
            ApiKeyAuthenticator auth,
            ResolutionStore store) =>
        {
            var identity = await auth.AuthenticateAsync(context.Request, context.RequestAborted);
            if (identity is null)
            {
                return Results.Json(
                    new { error = "unauthenticated", message = "Authentication is required." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!identity.Scopes.Contains("console:resolve", StringComparer.Ordinal))
            {
                return Results.Json(
                    new { error = "forbidden", message = "The console:resolve scope is required." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await store.ApplyApprovedAsync(
                id, body.ApproverStaffId, DateTimeOffset.UtcNow, context.RequestAborted);
            return result switch
            {
                ResolutionResult.Applied applied => Results.Json(new
                {
                    payment_id = applied.Payment.Id.Value,
                    state = PaymentStateText.Payment(applied.Payment.State),
                    applied = true,
                }),
                ResolutionResult.AlreadyApplied already => Results.Json(new
                {
                    payment_id = already.Payment.Id.Value,
                    state = PaymentStateText.Payment(already.Payment.State),
                    applied = false,
                }),
                ResolutionResult.SelfApproval => Results.Json(
                    new { error = "self_approval", message = "A person can't approve their own proposal." },
                    statusCode: StatusCodes.Status403Forbidden),
                ResolutionResult.NotProposed => Conflict("not_proposed", "The proposal isn't open."),
                ResolutionResult.StateChanged changed => Conflict(
                    "state_changed",
                    $"The payment is now {PaymentStateText.Payment(changed.Payment.State)}.",
                    changed.Payment.Id.Value),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });
    }

    private static IResult Conflict(string reason, string message, Guid? paymentId = null) =>
        Results.Json(
            new { error = reason, message, payment_id = paymentId },
            statusCode: StatusCodes.Status409Conflict);
}
