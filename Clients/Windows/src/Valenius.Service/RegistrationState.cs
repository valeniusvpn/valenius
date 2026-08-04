namespace Valenius.Service;

public class RegistrationState
{
    public bool IsActive { get; set; }
    public DateTime LastCheckedUtc { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>True once the user chose "Use standalone" in the first-run setup dialog.</summary>
    public bool StandaloneModeChosen { get; set; }

    /// <summary>
    /// True once every local profile that existed when this (previously standalone) client was
    /// activated has been successfully archived to the backend. Left false on partial failure so
    /// the next heartbeat/poll retries the whole batch — see
    /// <see cref="ClientRegistrationService.TransferLocalProfilesAsync"/>.
    /// </summary>
    public bool ProfilesTransferredToBackend { get; set; }
}
