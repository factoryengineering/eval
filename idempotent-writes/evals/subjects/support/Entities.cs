using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public class Ticket
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public string Subject { get; set; } = "";
    public int Priority { get; set; }
    public bool Escalated { get; set; }
    public string Category { get; set; } = "";
    public int ViewCount { get; set; }
    public DateTime? LastViewedAt { get; set; }
    public string ETag { get; set; } = "";
    public string CreationHash { get; set; } = "";
    public DateTime? DeletedAt { get; set; }
}

public class SupportContext : DbContext
{
    public SupportContext(DbContextOptions<SupportContext> options) : base(options) { }

    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(ticket =>
        {
            ticket.HasAlternateKey(entity => entity.PublicId);
            ticket.Property(entity => entity.ETag).IsConcurrencyToken();
            ticket.Property(entity => entity.CreationHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            ticket.HasQueryFilter(entity => entity.DeletedAt == null);
        });
    }
}
