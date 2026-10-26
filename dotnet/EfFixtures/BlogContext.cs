using Microsoft.EntityFrameworkCore;

namespace EfFixtures;

public class Blog
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Post> Posts { get; set; } = [];
    public List<Contributor> Contributors { get; set; } = [];
}

public class Post
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public int Views { get; set; }
    public int BlogId { get; set; }
    public Blog Blog { get; set; } = null!;
}

public class Contributor
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int BlogId { get; set; }
    public Blog Blog { get; set; } = null!;
}

public static class TitleRules
{
    public static bool HasPrefix(string title, string prefix) => title.StartsWith(prefix, StringComparison.Ordinal);
}

public class BlogContext(DbContextOptions<BlogContext> options) : DbContext(options)
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Contributor> Contributors => Set<Contributor>();
}
