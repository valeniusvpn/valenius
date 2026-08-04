using Microsoft.AspNetCore.Mvc;

namespace Valenius.Backend.Filters;

/// <summary>Builds the RFC 9457 Problem Details + stable <c>code</c> string error shape the
/// Management API uses (docs/design/management-api.md §8.3). Deliberately minimal for now
/// (no <c>type</c>/<c>instance</c> URLs, no <c>traceId</c> wiring) — enough for every
/// controller's own failures; the full envelope (used consistently across every Management
/// controller) can grow those fields later without a breaking response-shape change.</summary>
public static class ManagementProblem
{
    public static ObjectResult BadRequest(string code, string detail) =>
        Build(StatusCodes.Status400BadRequest, "Bad Request", code, detail);

    public static ObjectResult Unauthorized(string code, string detail) =>
        Build(StatusCodes.Status401Unauthorized, "Unauthorized", code, detail);

    public static ObjectResult Forbidden(string code, string detail) =>
        Build(StatusCodes.Status403Forbidden, "Forbidden", code, detail);

    public static ObjectResult Conflict(string code, string detail) =>
        Build(StatusCodes.Status409Conflict, "Conflict", code, detail);

    private static ObjectResult Build(int status, string title, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Title = title,
            Status = status,
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
