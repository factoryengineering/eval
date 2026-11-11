using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class ProblemExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ProblemException problem) return;
        var details = new ProblemDetails { Status = problem.Status, Title = problem.Message };
        context.HttpContext.Response.Headers.Remove("ETag");
        if (problem.ETag is not null) context.HttpContext.Response.Headers.ETag = $"\"{problem.ETag}\"";
        context.Result = new ObjectResult(details) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}

public static class AppSetup
{
    public static void ConfigureServices(IServiceCollection services, string sqliteConnectionString)
    {
        services.AddDbContext<SupportContext>(options => options.UseSqlite(sqliteConnectionString));
        services.AddScoped<TicketService>();
        services.Configure<MvcOptions>(options => options.Filters.Add<ProblemExceptionFilter>());
    }
}
