using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace HurryAppPleaseWork.Models
{
    public class AppDbContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<ProbResult> Results { get; set; }
        public DbSet<ProbRectTemplate> ResultsTemplate { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbRectTemplate>().ComplexProperty<Rect>(e => e.Rect, r =>
            {
                r.Ignore(r => r.Bottom);
                r.Ignore(r => r.BottomRight);
                r.Ignore(r => r.Top);
                r.Ignore(r => r.TopLeft);
                r.Ignore(r => r.Left);
                r.Ignore(r => r.Location);
                r.Ignore(r => r.Right);
                r.Ignore(r => r.Size);
            });
        }
    }
}
