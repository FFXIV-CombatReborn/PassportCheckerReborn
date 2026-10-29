namespace PassportCheckerReborn.Integrations.Tomestone;

public class TomestoneCharacterInfo
{
    public string? ProgPoint { get; set; }
    public string? DisplayPercent { get; set; }
    public int? TotalClears { get; set; }
    public string? CompletionWeek { get; set; }
    public double? BestPercent { get; set; }
    public bool NoLogs { get; set; }
    public bool NotFound { get; set; }
    public bool ActivityHidden { get; set; }
    public bool LookupFailed { get; set; }
}
