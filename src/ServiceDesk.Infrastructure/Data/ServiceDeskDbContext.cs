using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;

namespace ServiceDesk.Infrastructure.Data;

public class ServiceDeskDbContext : DbContext
{
    public ServiceDeskDbContext(DbContextOptions<ServiceDeskDbContext> options)
        : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CompanyService> CompanyServices => Set<CompanyService>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Ticket -> SubmittedBy relationship
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.SubmittedBy)
            .WithMany(e => e.SubmittedTickets)
            .HasForeignKey(t => t.SubmittedById)
            .OnDelete(DeleteBehavior.Restrict);

        // Ticket -> AssignedTo relationship
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.AssignedTo)
            .WithMany(e => e.AssignedTickets)
            .HasForeignKey(t => t.AssignedToId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ticket -> CompanyService relationship
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.CompanyService)
            .WithMany(s => s.RelatedTickets)
            .HasForeignKey(t => t.CompanyServiceId)
            .OnDelete(DeleteBehavior.SetNull);

        // Asset -> AssignedTo relationship
        modelBuilder.Entity<Asset>()
            .HasOne(a => a.AssignedTo)
            .WithMany(e => e.AssignedAssets)
            .HasForeignKey(a => a.AssignedToId)
            .OnDelete(DeleteBehavior.SetNull);

        // Unique constraint on Asset Tag
        modelBuilder.Entity<Asset>()
            .HasIndex(a => a.AssetTag)
            .IsUnique();

        // Unique constraint on Employee Email
        modelBuilder.Entity<Employee>()
            .HasIndex(e => e.Email)
            .IsUnique();
    }
}
