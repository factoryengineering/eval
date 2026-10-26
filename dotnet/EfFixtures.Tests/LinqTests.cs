using EfFixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public class LinqTests
{
    [Fact]
    public async Task TranslatesGroupedProjectionToSql()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<BlogContext>().UseSqlite(conn).Options;
        using var db = new BlogContext(options);
        db.Database.EnsureCreated();
        db.Blogs.Add(new Blog { Name = "b", Posts = [new Post { Title = "p", Views = 3 }, new Post { Title = "q", Views = 4 }] });
        db.SaveChanges();

        var query = db.Posts.AsNoTracking().GroupBy(p => p.BlogId).Select(g => new { g.Key, Total = g.Sum(p => p.Views) });
        Assert.Contains("GROUP BY", query.ToQueryString());
        Assert.Equal(7, (await query.SingleAsync()).Total);
    }
}
