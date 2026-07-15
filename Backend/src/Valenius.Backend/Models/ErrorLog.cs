using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class ErrorLog
{
    public int Id { get; set; }
    public DateTime OccurredAt { get; set; }

    [MaxLength(500)]
    public string? RequestPath { get; set; }

    [MaxLength(200)]
    public string? ExceptionType { get; set; }

    public string? Message { get; set; }
    public string? StackTrace { get; set; }
}
