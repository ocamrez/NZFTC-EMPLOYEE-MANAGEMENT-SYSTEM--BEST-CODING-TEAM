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

// Retrieve all employees
// Retrieve all employee records from PostgreSQL.
app.MapGet("/api/employees",
    async (
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    var employees = new List<object>();

    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        SELECT
            employeeid,
            name,
            email,
            department,
            job,
            status,
            salary,
            leavebalance,
            history,
            performance,
            training
        FROM employee_records
        ORDER BY employeeid;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    await using var reader =
        await command.ExecuteReaderAsync(cancellationToken);

    while (await reader.ReadAsync(cancellationToken))
    {
        employees.Add(new
        {
            employeeID = reader.GetInt32(0),
            name = reader.GetString(1),
            email = reader.IsDBNull(2) ? null : reader.GetString(2),
            department = reader.GetString(3),
            job = reader.GetString(4),
            status = reader.GetString(5),
            salary = reader.GetDecimal(6),
            leaveBalance = reader.GetDecimal(7),
            history = reader.IsDBNull(8) ? null : reader.GetString(8),
            performance = reader.IsDBNull(9) ? null : reader.GetString(9),
            training = reader.IsDBNull(10) ? null : reader.GetString(10)
        });
    }

    return Results.Ok(employees);
});
// Add a new employee to the PostgreSQL database.
app.MapPost("/api/employees",
    async (
        EmployeeCreateRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) ||
        string.IsNullOrWhiteSpace(request.Department) ||
        string.IsNullOrWhiteSpace(request.Job))
    {
        return Results.BadRequest(new
        {
            message = "Name, department and job are required."
        });
    }

    if (request.Salary < 0 || request.LeaveBalance < 0)
    {
        return Results.BadRequest(new
        {
            message = "Salary and leave balance cannot be negative."
        });
    }

    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        INSERT INTO employee_records
        (
            name,
            email,
            department,
            job,
            status,
            salary,
            leavebalance,
            history,
            performance,
            training
        )
        VALUES
        (
            @name,
            @email,
            @department,
            @job,
            @status,
            @salary,
            @leavebalance,
            @history,
            @performance,
            @training
        )
        RETURNING employeeid;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("name", request.Name);
    command.Parameters.AddWithValue(
        "email",
        (object?)request.Email ?? DBNull.Value);
    command.Parameters.AddWithValue("department", request.Department);
    command.Parameters.AddWithValue("job", request.Job);
    command.Parameters.AddWithValue("status", request.Status);
    command.Parameters.AddWithValue("salary", request.Salary);
    command.Parameters.AddWithValue("leavebalance", request.LeaveBalance);
    command.Parameters.AddWithValue(
        "history",
        (object?)request.History ?? DBNull.Value);
    command.Parameters.AddWithValue(
        "performance",
        (object?)request.Performance ?? DBNull.Value);
    command.Parameters.AddWithValue(
        "training",
        (object?)request.Training ?? DBNull.Value);

    var employeeID = (int)(await command.ExecuteScalarAsync(
        cancellationToken))!;

    return Results.Created(
        $"/api/employees/{employeeID}",
        new
        {
            success = true,
            message = "Employee created successfully.",
            employeeID
        });
});
// Update an existing employee in PostgreSQL.
app.MapPut("/api/employees/{id:int}",
    async (
        int id,
        EmployeeUpdateRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) ||
        string.IsNullOrWhiteSpace(request.Department) ||
        string.IsNullOrWhiteSpace(request.Job))
    {
        return Results.BadRequest(new
        {
            message = "Name, department and job are required."
        });
    }

    if (request.Salary < 0 || request.LeaveBalance < 0)
    {
        return Results.BadRequest(new
        {
            message = "Salary and leave balance cannot be negative."
        });
    }

    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        UPDATE employee_records
        SET
            name = @name,
            email = @email,
            department = @department,
            job = @job,
            status = @status,
            salary = @salary,
            leavebalance = @leavebalance,
            history = @history,
            performance = @performance,
            training = @training
        WHERE employeeid = @id;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("id", id);
    command.Parameters.AddWithValue("name", request.Name);
    command.Parameters.AddWithValue(
        "email", (object?)request.Email ?? DBNull.Value);
    command.Parameters.AddWithValue("department", request.Department);
    command.Parameters.AddWithValue("job", request.Job);
    command.Parameters.AddWithValue("status", request.Status);
    command.Parameters.AddWithValue("salary", request.Salary);
    command.Parameters.AddWithValue("leavebalance", request.LeaveBalance);
    command.Parameters.AddWithValue(
        "history", (object?)request.History ?? DBNull.Value);
    command.Parameters.AddWithValue(
        "performance", (object?)request.Performance ?? DBNull.Value);
    command.Parameters.AddWithValue(
        "training", (object?)request.Training ?? DBNull.Value);

    int rowsAffected = await command.ExecuteNonQueryAsync(
        cancellationToken);

    if (rowsAffected == 0)
    {
        return Results.NotFound(new
        {
            message = "Employee not found."
        });
    }

    return Results.Ok(new
    {
        success = true,
        message = "Employee updated successfully."
    });
});
app.Run();
public record EmployeeCreateRequest(
    string Name,
    string? Email,
    string Department,
    string Job,
    string Status,
    decimal Salary,
    decimal LeaveBalance,
    string? History,
    string? Performance,
    string? Training
);
public record EmployeeUpdateRequest(
    string Name,
    string? Email,
    string Department,
    string Job,
    string Status,
    decimal Salary,
    decimal LeaveBalance,
    string? History,
    string? Performance,
    string? Training
);
public record LoginRequest(string Username, string Password);
