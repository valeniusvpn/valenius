namespace Valenius.Service;

public sealed record RegistrationState
{
    public bool IsActive { get; init; }
    public DateTime LastCheckedUtc { get; init; }
    public Guid ClientId { get; init; }

    /// <summary>DPAPI-protected, reusable connection policy; never pending actions or credentials.</summary>
    public string? ProtectedConnectionPolicy { get; init; }

    /// <summary>True once the user chose "Use standalone" in the first-run setup dialog.</summary>
    public bool StandaloneModeChosen { get; init; }

    /// <summary>
    /// True once every local profile that existed when this (previously standalone) client was
    /// activated has been successfully archived to the backend. Left false on partial failure so
    /// the next heartbeat/poll retries the whole batch — see
    /// <see cref="ClientRegistrationService.TransferLocalProfilesAsync"/>.
    /// </summary>
    public bool ProfilesTransferredToBackend { get; init; }
}
