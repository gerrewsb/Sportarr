using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportarr.Api.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sportarr.Api.Tests.Helpers;

public static class DbContextTestHelper
{
    public static DbContextOptions<SportarrDbContext> GetTestingDbContextOptions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        return new DbContextOptionsBuilder<SportarrDbContext>()
            .UseSqlite(connection)
            .Options;
    }

    public static SportarrDbContext GetTestingDbContext()
    {
        var context = new SportarrDbContext(GetTestingDbContextOptions());
        context.Database.EnsureCreated();
        return context;
    }
}
