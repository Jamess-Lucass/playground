using System.Diagnostics;
using System.Reflection;
using System.Text;
using FluentValidation;
using HotChocolate.Diagnostics;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

var dbHost = Environment.GetEnvironmentVariable("POSTGRES_HOST");
var dbPort = Environment.GetEnvironmentVariable("POSTGRES_PORT");
var dbName = Environment.GetEnvironmentVariable("POSTGRES_DATABASE");
var dbUsername = Environment.GetEnvironmentVariable("POSTGRES_USERNAME");
var dbPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");

string connectionString = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUsername};Password={dbPassword};";

builder.Services.AddDbContext<ProductContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

builder.Host.UseSerilog((ctx, x) =>
{
    x.Enrich.FromLogContext();
    x.WriteTo.Console(new CustomLogFormatter());
    x.MinimumLevel.Information();
    x.MinimumLevel.Override("Microsoft.AspNetCore.Cors.Infrastructure.CorsService", LogEventLevel.Warning);
    x.MinimumLevel.Override("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogEventLevel.Warning);
    x.MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning);
});

builder.Services.AddCors();

builder.Services
    .AddGraphQLServer()
    .RegisterDbContext<ProductContext>()
    .AddApolloFederation()
    .AddQueryType<Query>()
    .AddMutationConventions(applyToAllMutations: true)
    .AddMutationType<Mutation>()
    .AddType<ErrorPayload>()
    .AddFiltering()
    .AddInstrumentation(x =>
    {
        x.IncludeDocument = true; // Include the request query body in the trace
        x.Scopes = ActivityScopes.All; // Include lots more GraphQL spans within trace
    });

builder.Services.AddOpenTelemetry()
    .ConfigureResource(x => x
        .AddService(builder.Environment.ApplicationName))
    .WithTracing(x => x
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddNpgsql()
        .AddHotChocolateInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.UseCors(opt => opt
    .AllowAnyHeader()
    .AllowAnyMethod()
    .SetIsOriginAllowed(origin => true)
    .AllowCredentials()
    .WithExposedHeaders("ETag")
);

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ProductContext>();
    // await context.Database.EnsureDeletedAsync();
    await context.Database.EnsureCreatedAsync();
}

app.MapGraphQL();

// Generate a schema.graphql file
// just for readability to inspect what is the schema output.
using (var scope = app.Services.CreateScope())
{
    var executor = scope.ServiceProvider.GetRequiredService<IRequestExecutorResolver>().GetRequestExecutorAsync().Result;
    var schema = executor.Schema.Print();
    File.WriteAllText("schema.graphql", schema);
}

app.Use(async (context, next) =>
{
    // Add trace id to response headers to ease of debugging.
    if (Activity.Current is not null)
    {
        context.Response.Headers.TryAdd("x-trace-id", Activity.Current.TraceId.ToString());
    }

    await next();
});

app.Run();

public class Query
{
    [UsePaging]
    [UseFiltering]
    public IQueryable<Product> GetProducts(ProductContext db, [Service] ILogger<Query> logger)
    {
        logger.LogInformation("Test");

        return db.Products.AsQueryable();
    }

    public ValueTask<Product?> GetProduct(ProductContext db, Guid id)
    {
        return db.Products.FindAsync(id);
    }
}

public class Mutation
{
    public async Task<IResponse> CreateProduct(CreateProductInput input, ProductContext db, [Service] ProductValidator validator)
    {
        var result = await validator.ValidateAsync(input);
        if (!result.IsValid)
        {
            return new ErrorPayload()
            {
                Errors = result.Errors.Select(x => new Error
                {
                    Message = x.ErrorMessage,
                    Path = x.PropertyName.ToCamelCase()
                })
            };
        }

        var product = new Product()
        {
            Name = input.Name,
            Description = input.Description,
            Price = input.Price,
        };

        await db.Products.AddAsync(product);
        await db.SaveChangesAsync();

        return product;
    }
}

public static class Convertors
{
    public static string ToCamelCase(this string str) => char.ToLowerInvariant(str[0]) + str[1..];
}