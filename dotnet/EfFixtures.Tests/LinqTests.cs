using EfFixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public class LinqTests
{
    [Fact]
    public async Task TranslatesGroupedProjectionToSql()
    {
        var cancellationToken = CancellationToken.None;
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<BlogContext>().UseSqlite(connection).Options;
        using var database = new BlogContext(options);
        await database.Database.EnsureCreatedAsync(cancellationToken);
        database.Blogs.Add(new Blog { Name = "b", Posts = [new Post { Title = "p", Views = 3 }, new Post { Title = "q", Views = 4 }] });
        await database.SaveChangesAsync(cancellationToken);

        var totalsQuery =
            from post in database.Posts
            group post by post.BlogId into blogPosts
            select new { BlogId = blogPosts.Key, TotalViews = blogPosts.Sum(item => item.Views) };

        Assert.Contains("GROUP BY", totalsQuery.ToQueryString());

        var blogTotal = await totalsQuery.SingleAsync(cancellationToken);
        Assert.Equal(7, blogTotal.TotalViews);
    }
}
