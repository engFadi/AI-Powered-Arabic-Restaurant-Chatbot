using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProjectE.Models.Entities;
using ProjectE.Models.Enums;

namespace ProjectE.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        { }

        public DbSet<UserEntity> Users { get; set; }
        public DbSet<ChatMessageEntity> ChatMessages { get; set; }
        public DbSet<OrderEntity> Orders { get; set; }
        public DbSet<OrderItemEntity> OrderItems { get; set; }
        public DbSet<MenuItemEntity> MenuItems { get; set; }
        public DbSet<FeedBackEntity> Feedbacks { get; set; }
        public DbSet<ConversationsEntity> Conversations { get; set; }
        public DbSet<ReservationEntity> Reservations { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Optional: set DB default values:
            modelBuilder.Entity<MenuItemEntity>().Property(m => m.IsAvailable).HasDefaultValue(true);
            modelBuilder.Entity<MenuItemEntity>().Property(m => m.Price).HasColumnType("decimal(18,2)");
        }
    }
}
