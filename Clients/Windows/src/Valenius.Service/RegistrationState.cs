namespace Valenius.Service;

public class RegistrationState
{
    public bool IsActive { get; set; }
    public DateTime LastCheckedUtc { get; set; }
    public Guid ClientId { get; set; }
}
