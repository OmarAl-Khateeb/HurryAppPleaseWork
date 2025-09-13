using OpenCvSharp;

namespace HurryAppPleaseWork.Models
{
    public class ProbResult
    {
        public int Id { get; set; }
        public string Username { get; set; } = default!;
        public byte[] ImageMatrix { get; set; } = default!;
        public ICollection<ProbRectTemplate> Templates { get; set; }
    }
    public class ProbRectTemplate
    {
        public int Id { get; set; }
        public RectRecord Rect { get; set; }
        public byte[] Template { get; set; }
    }

    public record RectRecord(int X, int Y, int Width, int Height)
    {
        public Rect ToRectangle() => new(this.X, Y, Width, Height);

        public static RectRecord FromRectangle(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
    }
}
