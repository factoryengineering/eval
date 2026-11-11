public static class WidgetApp
{
    public const string Source = """
        using System;
        using System.Linq;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;

        public class Widget { public int Id { get; set; } public Guid PublicId { get; set; } public string Name { get; set; } = ""; }

        public class WidgetContext : DbContext
        {
            public WidgetContext(DbContextOptions<WidgetContext> options) : base(options) { }
            public DbSet<Widget> Widgets => Set<Widget>();
        }

        public static class AppSetup
        {
            public static void ConfigureServices(IServiceCollection services, string sqliteConnectionString) =>
                services.AddDbContext<WidgetContext>(o => o.UseSqlite(sqliteConnectionString));
        }

        public record WidgetBody(Guid Id, string Name);

        [ApiController]
        [Route("api/widgets")]
        public class WidgetsController : ControllerBase
        {
            private readonly WidgetContext database;
            public WidgetsController(WidgetContext database) => this.database = database;

            [HttpGet("{id:guid}")]
            public IActionResult Get(Guid id) =>
                database.Widgets.Any(w => w.PublicId == id) ? Ok(new WidgetBody(id, "found")) : NotFound();

            [HttpPost]
            public IActionResult Post(WidgetBody body)
            {
                database.Widgets.Add(new Widget { PublicId = body.Id, Name = body.Name });
                database.SaveChanges();
                return Created($"/api/widgets/{body.Id}", body);
            }
        }
        """;

    public const string Broken = "public static class AppSetup { public static void ConfigureServices(Missing services, string connection) { } }";
}
