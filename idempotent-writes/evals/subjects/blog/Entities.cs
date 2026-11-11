using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public class Blog
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public string Name { get; set; } = "";
    public string ETag { get; set; } = "";
    public string CreationHash { get; set; } = "";
    public DateTime? DeletedAt { get; set; }
    public List<Post> Posts { get; set; } = new();
}

public class Post
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int BlogId { get; set; }
    public Blog Blog { get; set; } = null!;
    public string Title { get; set; } = "";
    public int Views { get; set; }
    public string ETag { get; set; } = "";
    public string CreationHash { get; set; } = "";
    public DateTime? DeletedAt { get; set; }
}

public class BlogContext : DbContext
{
    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }

    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Blog>(blog =>
        {
            blog.HasAlternateKey(entity => entity.PublicId);
            blog.Property(entity => entity.ETag).IsConcurrencyToken();
            blog.Property(entity => entity.CreationHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            blog.HasQueryFilter(entity => entity.DeletedAt == null);
        });
        modelBuilder.Entity<Post>(post =>
        {
            post.HasAlternateKey(entity => entity.PublicId);
            post.Property(entity => entity.ETag).IsConcurrencyToken();
            post.Property(entity => entity.CreationHash).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            post.HasOne(entity => entity.Blog).WithMany(entity => entity.Posts).HasForeignKey(entity => entity.BlogId);
            post.HasQueryFilter(entity => entity.DeletedAt == null);
        });
    }
}
