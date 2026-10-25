using System.ComponentModel.DataAnnotations;

namespace Concierge.Contracts;

public sealed class RideSchedulingOptions
{
    public const string SectionName = "Rides";
    public const string MinimumAdvanceNoticeKey = SectionName + ":MinimumAdvanceNoticeMinutes";

    [Range(1, int.MaxValue,
        ErrorMessage = MinimumAdvanceNoticeKey + " must be a positive whole number of minutes.")]
    public int MinimumAdvanceNoticeMinutes { get; set; } = 5;
}
