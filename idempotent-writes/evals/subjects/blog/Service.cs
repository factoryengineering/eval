using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public record BlogBody(Guid Id, string Name);
public record BlogResponse(Guid Id, string Name);
public record PostBody(Guid Id, Guid BlogId, string Title);
public record PostResponse(Guid Id, Guid BlogId, string Title, int Views);
public record Stored<T>(T Representation, string ETag, bool Created);

public class ProblemException : Exception
{
    public ProblemException(int status, string title, string? eTag = null) : base(title)
    {
        Status = status;
        ETag = eTag;
    }

    public int Status { get; }
    public string? ETag { get; }

    public static ProblemException BadRequest(string title) => new(400, title);
    public static ProblemException NotFound(string title) => new(404, title);
    public static ProblemException Conflict(string title) => new(409, title);
    public static ProblemException Gone(string title) => new(410, title);
    public static ProblemException PreconditionFailed(string title, string currentETag) => new(412, title, currentETag);
    public static ProblemException PreconditionRequired(string title) => new(428, title);
}

public static class Representation
{
    public static string Hash(params (string Name, string Value)[] fields)
    {
        var json = new StringBuilder("{");
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0) json.Append(',');
            Append(json, fields[index].Name);
            json.Append(':');
            Append(json, fields[index].Value);
        }
        json.Append('}');
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(BlogBody body) => Hash(("id", body.Id.ToString("D")), ("name", body.Name));

    public static string Hash(Guid id, Guid blogId, string title) =>
        Hash(("id", id.ToString("D")), ("blogId", blogId.ToString("D")), ("title", title));

    public static string Unquote(string? headerValue) => headerValue?.Trim().Trim('"') ?? "";

    private static void Append(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var character in text.Normalize(NormalizationForm.FormC))
        {
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case < ' ': json.Append("\\u").Append(((int)character).ToString("x4")); break;
                default: json.Append(character); break;
            }
        }
        json.Append('"');
    }
}

public class BlogService
{
    private readonly BlogContext database;

    public BlogService(BlogContext database) => this.database = database;

    public async Task<Stored<BlogResponse>> Create(BlogBody body, CancellationToken cancellationToken)
    {
        if (body.Id == Guid.Empty) throw ProblemException.BadRequest("The id is required.");
        if (string.IsNullOrWhiteSpace(body.Name)) throw ProblemException.BadRequest("The name is required.");
        var hash = Representation.Hash(body);
        var blog = new Blog { PublicId = body.Id, Name = body.Name, ETag = hash, CreationHash = hash };
        database.Blogs.Add(blog);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return new Stored<BlogResponse>(ToResponse(blog), blog.ETag, true);
        }
        catch (DbUpdateException)
        {
            database.Entry(blog).State = EntityState.Detached;
            var existingQuery = from stored in database.Blogs.IgnoreQueryFilters()
                                where stored.PublicId == body.Id
                                select stored;
            var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            if (existing.CreationHash != hash) throw ProblemException.Conflict("A blog with this id exists with different content.");
            if (existing.DeletedAt != null) throw ProblemException.Gone("The blog was deleted.");
            return new Stored<BlogResponse>(ToResponse(existing), existing.ETag, false);
        }
    }

    public async Task<Stored<BlogResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var blog = await Find(id, cancellationToken);
        return new Stored<BlogResponse>(ToResponse(blog), blog.ETag, false);
    }

    public async Task<string> Replace(Guid id, BlogBody body, string? ifMatch, CancellationToken cancellationToken)
    {
        if (body.Id != id) throw ProblemException.BadRequest("The body id must equal the route id.");
        if (string.IsNullOrWhiteSpace(body.Name)) throw ProblemException.BadRequest("The name is required.");
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        var hash = Representation.Hash(body);
        for (var attempt = 0; ; attempt++)
        {
            var blog = await Find(id, cancellationToken);
            if (blog.ETag == hash) return hash;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != blog.ETag)
                throw ProblemException.PreconditionFailed("The blog was changed by someone else.", blog.ETag);
            blog.Name = body.Name;
            blog.ETag = hash;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return hash;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.Entry(blog).State = EntityState.Detached;
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The blog was changed by someone else.", currentETag);
            }
        }
    }

    public async Task Delete(Guid id, string? ifMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        for (var attempt = 0; ; attempt++)
        {
            var blogQuery = from stored in database.Blogs
                            where stored.PublicId == id
                            select stored;
            var blog = await blogQuery.SingleOrDefaultAsync(cancellationToken);
            if (blog is null) return;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != blog.ETag)
                throw ProblemException.PreconditionFailed("The blog was changed by someone else.", blog.ETag);
            var deletedAt = DateTime.UtcNow;
            blog.DeletedAt = deletedAt;
            var postQuery = from post in database.Posts
                            where post.BlogId == blog.Id
                            select post;
            var posts = await postQuery.ToListAsync(cancellationToken);
            foreach (var post in posts) post.DeletedAt = deletedAt;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The blog was changed by someone else.", currentETag);
            }
        }
    }

    private async Task<Blog> Find(Guid id, CancellationToken cancellationToken)
    {
        var blogQuery = from stored in database.Blogs.IgnoreQueryFilters()
                        where stored.PublicId == id
                        select stored;
        var blog = await blogQuery.SingleOrDefaultAsync(cancellationToken);
        if (blog is null) throw ProblemException.NotFound("The blog does not exist.");
        if (blog.DeletedAt != null) throw ProblemException.Gone("The blog was deleted.");
        return blog;
    }

    private async Task<string> CurrentETag(Guid id, CancellationToken cancellationToken)
    {
        var eTagQuery = from stored in database.Blogs
                        where stored.PublicId == id
                        select stored.ETag;
        var eTags = await eTagQuery.ToListAsync(cancellationToken);
        return eTags.SingleOrDefault() ?? "";
    }

    private static BlogResponse ToResponse(Blog blog) => new(blog.PublicId, blog.Name);
}

public class PostService
{
    private readonly BlogContext database;

    public PostService(BlogContext database) => this.database = database;

    public async Task<Stored<PostResponse>> Create(Guid blogId, PostBody body, CancellationToken cancellationToken)
    {
        if (body.Id == Guid.Empty) throw ProblemException.BadRequest("The id is required.");
        if (body.BlogId != blogId) throw ProblemException.BadRequest("The body blogId must equal the route blogId.");
        if (string.IsNullOrWhiteSpace(body.Title)) throw ProblemException.BadRequest("The title is required.");
        var blogQuery = from stored in database.Blogs.IgnoreQueryFilters()
                        where stored.PublicId == blogId
                        select stored;
        var blog = await blogQuery.SingleOrDefaultAsync(cancellationToken);
        if (blog is null) throw ProblemException.NotFound("The blog does not exist.");
        if (blog.DeletedAt != null) throw ProblemException.Gone("The blog was deleted.");
        var hash = Representation.Hash(body.Id, blogId, body.Title);
        var post = new Post { PublicId = body.Id, BlogId = blog.Id, Title = body.Title, ETag = hash, CreationHash = hash };
        database.Posts.Add(post);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return new Stored<PostResponse>(ToResponse(post, blogId), post.ETag, true);
        }
        catch (DbUpdateException)
        {
            database.Entry(post).State = EntityState.Detached;
            var existingQuery = from stored in database.Posts.IgnoreQueryFilters()
                                join owner in database.Blogs.IgnoreQueryFilters() on stored.BlogId equals owner.Id
                                where stored.PublicId == body.Id
                                select new { Post = stored, OwnerPublicId = owner.PublicId };
            var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);
            if (existing is null) throw;
            if (existing.Post.CreationHash != hash) throw ProblemException.Conflict("A post with this id exists with different content.");
            if (existing.Post.DeletedAt != null) throw ProblemException.Gone("The post was deleted.");
            return new Stored<PostResponse>(ToResponse(existing.Post, existing.OwnerPublicId), existing.Post.ETag, false);
        }
    }

    public async Task<Stored<PostResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var (post, ownerId) = await Find(id, cancellationToken);
        return new Stored<PostResponse>(ToResponse(post, ownerId), post.ETag, false);
    }

    public async Task<string> Replace(Guid id, PostBody body, string? ifMatch, CancellationToken cancellationToken)
    {
        if (body.Id != id) throw ProblemException.BadRequest("The body id must equal the route id.");
        if (string.IsNullOrWhiteSpace(body.Title)) throw ProblemException.BadRequest("The title is required.");
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        var hash = Representation.Hash(body.Id, body.BlogId, body.Title);
        for (var attempt = 0; ; attempt++)
        {
            var (post, ownerId) = await Find(id, cancellationToken);
            if (ownerId != body.BlogId) throw ProblemException.Conflict("The blog of a post cannot be changed.");
            if (post.ETag == hash) return hash;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != post.ETag)
                throw ProblemException.PreconditionFailed("The post was changed by someone else.", post.ETag);
            post.Title = body.Title;
            post.ETag = hash;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return hash;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.Entry(post).State = EntityState.Detached;
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The post was changed by someone else.", currentETag);
            }
        }
    }

    public async Task Delete(Guid id, string? ifMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null) throw ProblemException.PreconditionRequired("The If-Match header is required.");
        for (var attempt = 0; ; attempt++)
        {
            var postQuery = from stored in database.Posts
                            where stored.PublicId == id
                            select stored;
            var post = await postQuery.SingleOrDefaultAsync(cancellationToken);
            if (post is null) return;
            if (ifMatch.Trim() != "*" && Representation.Unquote(ifMatch) != post.ETag)
                throw ProblemException.PreconditionFailed("The post was changed by someone else.", post.ETag);
            post.DeletedAt = DateTime.UtcNow;
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                database.ChangeTracker.Clear();
            }
            catch (DbUpdateConcurrencyException)
            {
                var currentETag = await CurrentETag(id, cancellationToken);
                throw ProblemException.PreconditionFailed("The post was changed by someone else.", currentETag);
            }
        }
    }

    private async Task<(Post Post, Guid OwnerId)> Find(Guid id, CancellationToken cancellationToken)
    {
        var postQuery = from stored in database.Posts.IgnoreQueryFilters()
                        join owner in database.Blogs.IgnoreQueryFilters() on stored.BlogId equals owner.Id
                        where stored.PublicId == id
                        select new { Post = stored, OwnerPublicId = owner.PublicId };
        var found = await postQuery.SingleOrDefaultAsync(cancellationToken);
        if (found is null) throw ProblemException.NotFound("The post does not exist.");
        if (found.Post.DeletedAt != null) throw ProblemException.Gone("The post was deleted.");
        return (found.Post, found.OwnerPublicId);
    }

    private async Task<string> CurrentETag(Guid id, CancellationToken cancellationToken)
    {
        var eTagQuery = from stored in database.Posts
                        where stored.PublicId == id
                        select stored.ETag;
        var eTags = await eTagQuery.ToListAsync(cancellationToken);
        return eTags.SingleOrDefault() ?? "";
    }

    private static PostResponse ToResponse(Post post, Guid blogId) => new(post.PublicId, blogId, post.Title, post.Views);
}
