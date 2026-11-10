using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EfFixtures;
using Microsoft.EntityFrameworkCore;

public record BlogTotal(string BlogName, int TotalViews);

public static class BlogQueries
{
    public static async Task<List<string>> PopularTitles(BlogContext database, int minViews, CancellationToken cancellationToken)
    {
        var titlesQuery =
            from post in database.Posts
            where post.Views > minViews
            orderby post.Views descending
            select post.Title;
        return await titlesQuery.ToListAsync(cancellationToken);
    }

    public static async Task<List<BlogTotal>> TotalViewsPerBlog(BlogContext database, CancellationToken cancellationToken)
    {
        var totalsQuery =
            from blog in database.Blogs
            orderby blog.Name
            select new BlogTotal(blog.Name, blog.Posts.Sum(post => post.Views));
        return await totalsQuery.ToListAsync(cancellationToken);
    }

    public static async Task<int> TotalPostViews(BlogContext database, CancellationToken cancellationToken) =>
        await database.Posts.SumAsync(post => post.Views, cancellationToken);
}
