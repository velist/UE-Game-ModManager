namespace UEModManager.Models
{
    public enum BackgroundMode
    {
        Gradient = 0,
        Image = 1,
        SolidColor = 2
    }

    public class BackgroundSettings
    {
        public BackgroundMode Mode { get; set; } = BackgroundMode.Gradient;
        public string? ImagePath { get; set; }
        public string? SolidColor { get; set; } = "#030303";
        public double Opacity { get; set; } = 0.7;
        public double BlurRadius { get; set; } = 0.0;
        public bool ApplyToDialogs { get; set; } = true;

        public BackgroundSettings Clone() => new()
        {
            Mode = Mode,
            ImagePath = ImagePath,
            SolidColor = SolidColor,
            Opacity = Opacity,
            BlurRadius = BlurRadius,
            ApplyToDialogs = ApplyToDialogs
        };
    }
}
