using GuildHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GuildHub.Bot.Data;

public sealed class GuildHubDbContextFactory : IDesignTimeDbContextFactory<GuildHubDbContext>
{
    public GuildHubDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=guildhub;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<GuildHubDbContext>();
        optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly("GuildHub.Core"));

        return new GuildHubDbContext(optionsBuilder.Options);
    }
}
