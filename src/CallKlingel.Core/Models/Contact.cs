namespace CallKlingel.Core.Models;

public sealed record Contact
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string PhoneNumber { get; init; }
    public string? ImagePath { get; init; }
}
