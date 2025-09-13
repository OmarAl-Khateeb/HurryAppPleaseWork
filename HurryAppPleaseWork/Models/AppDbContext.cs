using Microsoft.EntityFrameworkCore;

namespace HurryAppPleaseWork.Models
{
    public class AppDbContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<ProbResult> Results { get; set; }
        public DbSet<ProbRectTemplate> ResultsTemplate { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbRectTemplate>().OwnsOne(e => e.Rect);
        }
    }
}
