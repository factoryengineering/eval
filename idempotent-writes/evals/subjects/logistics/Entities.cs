using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public class Depot
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public string Code { get; set; } = "";
    public int CapacityCubicMeters { get; set; }
    public DateTime? LastAuditAt { get; set; }
    public string ETag { get; set; } = "";
    public string CreationHash { get; set; } = "";
    public DateTime? DeletedAt { get; set; }
    public List<Consignment> Consignments { get; set; } = new();
}

public class Consignment
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int DepotId { get; set; }
    public Depot Depot { get; set; } = null!;
    public string Reference { get; set; } = "";
    public decimal WeightKg { get; set; }
    public bool Fragile { get; set; }
    public int DownloadCount { get; set; }
    public string ETag { get; set; } = "";
    public string CreationHash { get; set; } = "";
    public DateTime? DeletedAt { get; set; }
}

public class LogisticsContext : DbContext
{
    public LogisticsContext(DbContextOptions<LogisticsContext> options) : base(options) { }

    public DbSet<Depot> Depots => Set<Depot>();
    public DbSet<Consignment> Consignments => Set<Consignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Depot>(depot =>
        {
            depot.HasAlternateKey(entity => entity.PublicId);
            depot.Property(entity => entity.ETag).IsConcurrencyToken();
            depot.Property(entity => entity.DeletedAt).IsConcurrencyToken();
            depot.Property(entity => entity.CreationHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            depot.HasQueryFilter(entity => entity.DeletedAt == null);
        });
        modelBuilder.Entity<Consignment>(consignment =>
        {
            consignment.HasAlternateKey(entity => entity.PublicId);
            consignment.Property(entity => entity.ETag).IsConcurrencyToken();
            consignment.Property(entity => entity.DeletedAt).IsConcurrencyToken();
            consignment.Property(entity => entity.CreationHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            consignment.HasOne(entity => entity.Depot).WithMany(entity => entity.Consignments).HasForeignKey(entity => entity.DepotId);
            consignment.HasQueryFilter(entity => entity.DeletedAt == null);
        });
    }
}
