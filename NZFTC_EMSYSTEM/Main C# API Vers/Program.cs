using Npgsql;
using NZFTC_EMSYSTEM_API.Database;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI configuration
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddPolicy("NZFTCFrontend", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5500",
                "http://127.0.0.1:5500"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Retrieve database password from environment
string databasePassword =
    Environment.GetEnvironmentVariable("databasePassword")
    ?? throw new InvalidOperationException(
        "Database password environment variable not found."
    );

// PostgreSQL connection configuration
var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = "ep-young-surf-bk3w9gws-pooler.c-2.ap-southeast-2.aws.neon.tech",
    Port = 5432,
    Database = "neondb",
    Username = "neondb_owner",
    Password = databasePassword,
    SslMode = SslMode.Require
}.ConnectionString;

// Create PostgreSQL data source
var dataSourceBuilder =
    new NpgsqlDataSourceBuilder(connectionString);

var dataSource = dataSourceBuilder.Build();

// Register database services
builder.Services.AddSingleton(dataSource);
builder.Services.AddScoped<DatabaseService>();

var app = builder.Build();
app.UseCors("NZFTCFrontend");

// OpenAPI documentation
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// API homepage
app.MapGet("/", () =>
{
    return Results.Ok(new
    {
        message = "NZFTC Employee Management API is running."
    });
});

// Database connection test
app.MapGet("/api/health/database",
    async (
        DatabaseService database,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
{
    try
    {
        bool connected =
            await database.CheckConnectionAsync(cancellationToken);

        return Results.Ok(new
        {
            status = connected ? "Connected" : "Disconnected",
            database = "PostgreSQL"
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database connection test failed.");

        return Results.Problem(
            title: "Database connection failed.",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }
});
// User login endpoint
app.MapPost("/api/auth/login",
    async (
        LoginRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        SELECT userid, username, role
        FROM users
        WHERE username = @username
        AND password = @password;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("username", request.Username);
    command.Parameters.AddWithValue("password", request.Password);

    await using var reader =
        await command.ExecuteReaderAsync(cancellationToken);

    if (await reader.ReadAsync(cancellationToken))
    {
        return Results.Ok(new
        {
            success = true,
            message = "Login successful.",
            userID = reader.GetInt32(0),
            username = reader.GetString(1),
            role = reader.GetString(2)
        });
    }

    return Results.Unauthorized();
});

app.Run();
public record LoginRequest(string Username, string Password);
