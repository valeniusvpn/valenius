namespace Valenius.Backend.Licensing;

public sealed class LicenseLimitException : Exception
{
    public string Reason { get; }
    public LicenseLimitException(string reason) : base(reason) => Reason = reason;
}
