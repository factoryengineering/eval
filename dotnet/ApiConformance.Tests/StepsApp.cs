public static class StepsApp
{
    public const string Source = """
        using System;
        using System.Collections.Concurrent;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;

        public class Widget { public int Id { get; set; } public string Name { get; set; } = ""; }

        public class StepContext : DbContext
        {
            public StepContext(DbContextOptions<StepContext> options) : base(options) { }
            public DbSet<Widget> Widgets => Set<Widget>();
        }

        public static class AppSetup
        {
            public static readonly ConcurrentQueue<string> Log = new();
            public static readonly System.Threading.SemaphoreSlim Gate = new(1, 1);
            public static void ConfigureServices(IServiceCollection services, string sqliteConnectionString) =>
                services.AddDbContext<StepContext>(o => o.UseSqlite(sqliteConnectionString));
        }

        [ApiController]
        [Route("api/steps")]
        public class StepsController : ControllerBase
        {
            private readonly StepContext database;
            public StepsController(StepContext database) => this.database = database;

            [HttpGet("{tag}")]
            public async Task<IActionResult> Walk(string tag)
            {
                for (var step = 1; step <= 3; step++)
                {
                    await database.Widgets.CountAsync(w => w.Name == tag);
                    AppSetup.Log.Enqueue($"{tag}{step}");
                }
                database.Widgets.Add(new Widget { Name = tag });
                await database.SaveChangesAsync();
                AppSetup.Log.Enqueue($"{tag}4");
                return Ok();
            }

            [HttpGet("tx/{tag}")]
            public async Task<IActionResult> Transact(string tag)
            {
                await using var transaction = await database.Database.BeginTransactionAsync();
                await database.Database.ExecuteSqlRawAsync("UPDATE \"Widgets\" SET \"Name\" = {0}", tag + "1");
                await database.Database.ExecuteSqlRawAsync("UPDATE \"Widgets\" SET \"Name\" = {0}", tag + "2");
                await transaction.CommitAsync();
                return Ok();
            }

            [HttpGet("locked/{tag}")]
            public async Task<IActionResult> Locked(string tag)
            {
                await AppSetup.Gate.WaitAsync();
                try
                {
                    await database.Widgets.CountAsync(w => w.Name == tag);
                    database.Widgets.Add(new Widget { Name = tag });
                    await database.SaveChangesAsync();
                }
                finally { AppSetup.Gate.Release(); }
                return Ok();
            }

            [HttpGet("hang")]
            public async Task<IActionResult> Hang()
            {
                await Task.Delay(System.Threading.Timeout.Infinite, HttpContext.RequestAborted);
                return Ok();
            }

            [HttpGet("log")]
            public IActionResult ReadLog() => Ok(AppSetup.Log.ToArray());
        }
        """;
}
