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
        public Rect Rect { get; set; }
        public byte[] Template { get; set; }
    }
}
