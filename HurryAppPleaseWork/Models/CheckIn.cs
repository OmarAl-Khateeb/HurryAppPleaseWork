namespace HurryAppPleaseWork.Models
{
    public class CheckIn
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public required int UserId { get; set; }
        public User User { get; set; } = default!;

        public required int ProbResultId { get; set; }
        public ProbResult ProbResult { get; set; } = default!;

        public required double ResultScore { get; set; }
        public required byte[] ImageMatrix { get; set; }
    }
}