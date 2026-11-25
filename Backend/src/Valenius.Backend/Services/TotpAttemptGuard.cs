using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using OtpNet;

namespace Valenius.Backend.Services;

public enum TotpAttemptResult { Rejected, Accepted, Locked }

/// <summary>Shared database arbitration for a secret/state-bound principal query.</summary>
public static class TotpAttemptGuard
{
    public const int MaxFailures = 5;
    public const int WindowMinutes = 15;

    // Both principal models use these names; callers bind identity, current secret
    // and enabled/enrollment state in the query before passing it to this guard.
    public static async Task<TotpAttemptResult> VerifyAsync<T>(IQueryable<T> principal,
        string secret, string? code, string replayProperty,
        Action<UpdateSettersBuilder<T>>? onAccepted = null) where T : class
    {
        var now = DateTime.UtcNow;
        var eligible = principal.Where(p => EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt") == null
            || EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt") <= now
            || EF.Property<int>(p, "TotpFailureCount") < MaxFailures);

        if (TryVerify(secret, code, out var interval))
        {
            var accepted = await eligible
                .Where(p => EF.Property<long?>(p, replayProperty) == null
                         || EF.Property<long?>(p, replayProperty) < interval)
                .ExecuteUpdateAsync(set =>
                {
                    set.SetProperty(p => EF.Property<long?>(p, replayProperty), interval)
                       .SetProperty(p => EF.Property<int>(p, "TotpFailureCount"), 0)
                       .SetProperty(p => EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt"), (DateTime?)null);
                    onAccepted?.Invoke(set);
                });
            if (accepted == 1) return TotpAttemptResult.Accepted;
        }

        // Count invalid codes AND replay attempts atomically. Locked requests do not
        // increment or extend the fixed window, and a changed secret/state matches no row.
        var endsAt = now.AddMinutes(WindowMinutes);
        await eligible.ExecuteUpdateAsync(set => set
            .SetProperty(p => EF.Property<int>(p, "TotpFailureCount"), p =>
                EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt") == null
                || EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt") <= now
                    ? 1 : EF.Property<int>(p, "TotpFailureCount") + 1)
            .SetProperty(p => EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt"), p =>
                EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt") == null
                || EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt") <= now
                    ? endsAt : EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt")));
        var state = await principal.Select(p => new
        {
            Count = EF.Property<int>(p, "TotpFailureCount"),
            EndsAt = EF.Property<DateTime?>(p, "TotpFailureWindowEndsAt")
        }).SingleOrDefaultAsync();
        return state is { Count: >= MaxFailures } && state.EndsAt > DateTime.UtcNow
            ? TotpAttemptResult.Locked : TotpAttemptResult.Rejected;
    }

    private static bool TryVerify(string secret, string? code, out long interval)
    {
        interval = 0;
        code = code?.Trim();
        if (code is not { Length: 6 } || code.Any(c => c is < '0' or > '9') || string.IsNullOrEmpty(secret))
            return false;
        try
        {
            return new Totp(Base32Encoding.ToBytes(secret)).VerifyTotp(code, out interval,
                VerificationWindow.RfcSpecifiedNetworkDelay);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return false; }
    }
}
