namespace HurryAppPleaseWork.Models
{
    public class CheckIn
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int UserId { get; set; }
        public User User { get; set; } = default!;

        public int ProbResultId { get; set; }
        public ProbResult ProbResult { get; set; } = default!;

        public double ResultScore { get; set; }
        public required byte[] ImageMatrix { get; set; }
    }
}