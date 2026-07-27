namespace DroidBox.Core.Models;

public sealed class AndroidVersion
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string GoldenImageUrl { get; init; }
    public required string GoldenImageSha256 { get; init; }
    public int RamMb { get; init; } = 1024;
    public int DiskGb { get; init; } = 8;
}
