namespace PetGPT.Models;

public sealed class AppSettings
{
    public double? PetLeft { get; set; }
    public double? PetTop { get; set; }
    public double BubbleWidth { get; set; } = 500;
    public double BubbleHeight { get; set; } = 650;
    public bool CompactMode { get; set; } = true;
}
