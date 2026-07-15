namespace Valenius.Backend.Helpers;

public static class DateTimeExtensions
{
    private static readonly TimeZoneInfo Vienna =
        TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    public static DateTime ToVienna(this DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc,
            Vienna);
}
