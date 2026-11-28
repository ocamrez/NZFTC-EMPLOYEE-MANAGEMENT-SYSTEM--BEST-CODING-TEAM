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
// Retrieve information belonging to a specific employee.
app.MapGet("/api/employees/{userID:int}/information",
    async (
        int userID,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        SELECT u.username, e.salary, e.leavebalance
        FROM users u
        JOIN employees e ON u.userid = e.userid
        WHERE u.userid = @userID;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("userID", userID);

    await using var reader =
        await command.ExecuteReaderAsync(cancellationToken);

    if (await reader.ReadAsync(cancellationToken))
    {
        return Results.Ok(new
        {
            success = true,
            username = reader.GetString(0),
            salary = reader.GetDecimal(1),
            leaveBalance = reader.GetDecimal(2)
        });
    }

    return Results.NotFound(new
    {
        success = false,
        message = "Employee information not found."
    });
});
// Allow an employee to update their own information.
app.MapPut("/api/employees/{userID:int}/information",
    async (
        int userID,
        EmployeeInformationUpdateRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) ||
        string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new
        {
            message = "Username and password are required."
        });
    }

    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        UPDATE users
        SET username = @username,
            password = @password
        WHERE userid = @userID
        AND role = 'Employee';

        UPDATE employees
        SET email = @email
        WHERE userid = @userID;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("userID", userID);
    command.Parameters.AddWithValue("username", request.Username);
    command.Parameters.AddWithValue("password", request.Password);
    command.Parameters.AddWithValue(
        "email",
        (object?)request.Email ?? DBNull.Value);

    await command.ExecuteNonQueryAsync(cancellationToken);

    return Results.Ok(new
    {
        success = true,
        message = "Employee information updated successfully."
    });
});
// Allow employees to submit leave applications.
app.MapPost("/api/employees/{userID:int}/leave",
    async (
        int userID,
        LeaveRequestCreate request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.LeaveType))
    {
        return Results.BadRequest(new
        {
            message = "Leave type is required."
        });
    }

    if (request.EndDate < request.StartDate)
    {
        return Results.BadRequest(new
        {
            message = "End date cannot be earlier than start date."
        });
    }

    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    // Check that the employee account exists.
    string checkSql = @"
        SELECT COUNT(*)
        FROM users
        WHERE userid = @userID
        AND role = 'Employee';";

    await using var checkCommand =
        new NpgsqlCommand(checkSql, connection);

    checkCommand.Parameters.AddWithValue("userID", userID);

    int employeeExists = Convert.ToInt32(
        await checkCommand.ExecuteScalarAsync(cancellationToken));

    if (employeeExists == 0)
    {
        return Results.NotFound(new
        {
            message = "Employee account not found."
        });
    }

    string sql = @"
        INSERT INTO leave_requests
        (
            userid,
            leavetype,
            startdate,
            enddate,
            reason
        )
        VALUES
        (
            @userID,
            @leaveType,
            @startDate,
            @endDate,
            @reason
        )
        RETURNING requestid;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("userID", userID);
    command.Parameters.AddWithValue("leaveType", request.LeaveType);
    command.Parameters.AddWithValue("startDate", request.StartDate);
    command.Parameters.AddWithValue("endDate", request.EndDate);
    command.Parameters.AddWithValue(
        "reason",
        (object?)request.Reason ?? DBNull.Value);

    int requestID = Convert.ToInt32(
        await command.ExecuteScalarAsync(cancellationToken));

    return Results.Created(
        $"/api/employees/{userID}/leave/{requestID}",
        new
        {
            success = true,
            message = "Leave application submitted successfully.",
            requestID,
            status = "Pending"
        });
});
// Retrieve payslips belonging to a specific employee.
app.MapGet("/api/employees/{userID:int}/payslips",
    async (
        int userID,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        SELECT
            payslipid,
            payperiod_start,
            payperiod_end,
            grosssalary,
            taxdeductions,
            otherdeductions,
            netsalary,
            paymentdate
        FROM payslips
        WHERE userid = @userID
        ORDER BY paymentdate DESC;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("userID", userID);

    await using var reader =
        await command.ExecuteReaderAsync(cancellationToken);

    var payslips = new List<object>();

    while (await reader.ReadAsync(cancellationToken))
    {
        payslips.Add(new
        {
            payslipID = reader.GetInt32(0),
            payPeriodStart = reader.GetDateTime(1),
            payPeriodEnd = reader.GetDateTime(2),
            grossSalary = reader.GetDecimal(3),
            taxDeductions = reader.GetDecimal(4),
            otherDeductions = reader.GetDecimal(5),
            netSalary = reader.GetDecimal(6),
            paymentDate = reader.GetDateTime(7)
        });
    }

    return Results.Ok(payslips);
});
// Retrieve the company holiday list.
app.MapGet("/api/holidays",
    async (
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
{
    await using var connection =
        await dataSource.OpenConnectionAsync(cancellationToken);

    string sql = @"
        SELECT
            holidayid,
            holidayname,
            holidaydate,
            description
        FROM company_holidays
        ORDER BY holidaydate;";

    await using var command =
        new NpgsqlCommand(sql, connection);

    await using var reader =
        await command.ExecuteReaderAsync(cancellationToken);

    var holidays = new List<object>();

    while (await reader.ReadAsync(cancellationToken))
    {
        holidays.Add(new
        {
            holidayID = reader.GetInt32(0),
            holidayName = reader.GetString(1),
            holidayDate = reader.GetDateTime(2),
            description = reader.IsDBNull(3)
                ? null
                : reader.GetString(3)
        });
    }

    return Results.Ok(holidays);
});
// ADMIN: Add a company holiday
app.MapPost("/api/admin/holidays", async (
    HolidayCreateRequest request,
    NpgsqlDataSource dataSource) =>
{
    if (string.IsNullOrWhiteSpace(request.HolidayName))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Holiday name is required."
        });
    }

    if (request.HolidayDate == default)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "A valid holiday date is required."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        INSERT INTO company_holidays
            (holidayname, holidaydate, description)
        VALUES
            (@holidayName, @holidayDate, @description)
        RETURNING holidayid;
    ";

    await using var command = new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("holidayName", request.HolidayName);
    command.Parameters.AddWithValue("holidayDate", request.HolidayDate);

    command.Parameters.AddWithValue(
        "description",
        (object?)request.Description ?? DBNull.Value
    );

    int holidayID = (int)(await command.ExecuteScalarAsync())!;

    return Results.Ok(new
    {
        success = true,
        message = "Company holiday added successfully.",
        holidayID,
        holidayName = request.HolidayName,
        holidayDate = request.HolidayDate,
        description = request.Description
    });
});
// ADMIN: Update an existing company holiday
app.MapPut("/api/admin/holidays/{holidayID:int}", async (
    int holidayID,
    HolidayUpdateRequest request,
    NpgsqlDataSource dataSource) =>
{
    if (string.IsNullOrWhiteSpace(request.HolidayName))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Holiday name is required."
        });
    }

    if (request.HolidayDate == default)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "A valid holiday date is required."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        UPDATE company_holidays
        SET holidayname = @holidayName,
            holidaydate = @holidayDate,
            description = @description
        WHERE holidayid = @holidayID;
    ";

    await using var command = new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("holidayID", holidayID);
    command.Parameters.AddWithValue("holidayName", request.HolidayName);
    command.Parameters.AddWithValue("holidayDate", request.HolidayDate);
    command.Parameters.AddWithValue(
        "description",
        (object?)request.Description ?? DBNull.Value
    );

    int rowsAffected = await command.ExecuteNonQueryAsync();

    if (rowsAffected == 0)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Holiday not found."
        });
    }

    return Results.Ok(new
    {
        success = true,
        message = "Company holiday updated successfully.",
        holidayID
    });
});
// ADMIN: Delete a company holiday
app.MapDelete("/api/admin/holidays/{holidayID:int}", async (
    int holidayID,
    NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        DELETE FROM company_holidays
        WHERE holidayid = @holidayID;
    ";

    await using var command = new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("holidayID", holidayID);

    int rowsAffected = await command.ExecuteNonQueryAsync();

    if (rowsAffected == 0)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Holiday not found."
        });
    }

    return Results.Ok(new
    {
        success = true,
        message = "Company holiday deleted successfully.",
        holidayID
    });
});
// ADMIN: View all employee leave applications
app.MapGet("/api/admin/leave-requests", async (NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        SELECT
            lr.requestid,
            lr.userid,
            u.username,
            lr.leavetype,
            lr.startdate,
            lr.enddate,
            lr.reason,
            lr.status,
            lr.submitted_at
        FROM leave_requests lr
        INNER JOIN users u ON lr.userid = u.userid
        ORDER BY lr.submitted_at DESC;
    ";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var leaveRequests = new List<object>();

    while (await reader.ReadAsync())
    {
        leaveRequests.Add(new
        {
            requestID = reader.GetInt32(0),
            userID = reader.GetInt32(1),
            username = reader.GetString(2),
            leaveType = reader.GetString(3),
            startDate = reader.GetDateTime(4),
            endDate = reader.GetDateTime(5),
            reason = reader.IsDBNull(6) ? null : reader.GetString(6),
            status = reader.GetString(7),
            submittedAt = reader.GetDateTime(8)
        });
    }

    return Results.Ok(leaveRequests);
});
// ADMIN: Approve or reject leave applications
app.MapPut("/api/admin/leave-requests/{requestID:int}", async (
    int requestID,
    LeaveRequestUpdate request,
    NpgsqlDataSource dataSource) =>
{
    if (request.Status != "Approved" && request.Status != "Rejected")
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Status must be Approved or Rejected."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    // Find the pending leave application and lock its row
    const string selectSql = @"
        SELECT userid, startdate, enddate
        FROM leave_requests
        WHERE requestid = @requestID
        AND status = 'Pending'
        FOR UPDATE;
    ";

    int userID;
    DateTime startDate;
    DateTime endDate;

    await using (var selectCommand = new NpgsqlCommand(
        selectSql, connection, transaction))
    {
        selectCommand.Parameters.AddWithValue("requestID", requestID);

        await using var reader = await selectCommand.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Results.NotFound(new
            {
                success = false,
                message = "Leave request not found or already processed."
            });
        }

        userID = reader.GetInt32(0);
        startDate = reader.GetDateTime(1);
        endDate = reader.GetDateTime(2);
    }

    // Rejecting a request does not deduct leave
    if (request.Status == "Rejected")
    {
        const string rejectSql = @"
            UPDATE leave_requests
            SET status = 'Rejected'
            WHERE requestid = @requestID;
        ";

        await using var rejectCommand = new NpgsqlCommand(
            rejectSql, connection, transaction);

        rejectCommand.Parameters.AddWithValue("requestID", requestID);

        await rejectCommand.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        return Results.Ok(new
        {
            success = true,
            message = "Leave request successfully rejected.",
            requestID,
            status = "Rejected"
        });
    }

    // Calculate working days (Monday to Friday)
    int workingDays = 0;

    for (DateTime date = startDate.Date;
         date <= endDate.Date;
         date = date.AddDays(1))
    {
        if (date.DayOfWeek != DayOfWeek.Saturday &&
            date.DayOfWeek != DayOfWeek.Sunday)
        {
            workingDays++;
        }
    }

    if (workingDays == 0)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "No working days found in this leave period."
        });
    }

    // Deduct leave from the employee's balance
    const string deductionSql = @"
        UPDATE employees
        SET leavebalance = leavebalance - @days
        WHERE userid = @userID
        AND leavebalance >= @days;
    ";

    await using (var deductionCommand = new NpgsqlCommand(
        deductionSql, connection, transaction))
    {
        deductionCommand.Parameters.AddWithValue("days", workingDays);
        deductionCommand.Parameters.AddWithValue("userID", userID);

        int rowsAffected = await deductionCommand.ExecuteNonQueryAsync();

        if (rowsAffected == 0)
        {
            return Results.BadRequest(new
            {
                success = false,
                message = "Employee has insufficient leave balance or no employee record exists."
            });
        }
    }

    // Update application status
    const string approveSql = @"
        UPDATE leave_requests
        SET status = 'Approved'
        WHERE requestid = @requestID;
    ";

    await using (var approveCommand = new NpgsqlCommand(
        approveSql, connection, transaction))
    {
        approveCommand.Parameters.AddWithValue("requestID", requestID);
        await approveCommand.ExecuteNonQueryAsync();
    }

    // Save both changes together
    await transaction.CommitAsync();

    return Results.Ok(new
    {
        success = true,
        message = "Leave request approved and balance deducted successfully.",
        requestID,
        status = "Approved",
        daysDeducted = workingDays
    });
});
// EMPLOYEE: Submit a grievance
app.MapPost("/api/employees/{userID:int}/grievances", async (
    int userID,
    GrievanceCreateRequest request,
    NpgsqlDataSource dataSource) =>
{
    if (string.IsNullOrWhiteSpace(request.Subject) ||
        string.IsNullOrWhiteSpace(request.Description))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Subject and description are required."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();

    // Check that the employee exists
    const string checkSql = @"
        SELECT COUNT(*)
        FROM users
        WHERE userid = @userID
        AND role = 'Employee';
    ";

    await using var checkCommand = new NpgsqlCommand(checkSql, connection);
    checkCommand.Parameters.AddWithValue("userID", userID);

    long employeeExists = (long)(await checkCommand.ExecuteScalarAsync())!;

    if (employeeExists == 0)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Employee account not found."
        });
    }

    // Save the grievance
    const string insertSql = @"
        INSERT INTO grievances
            (userid, subject, description)
        VALUES
            (@userID, @subject, @description)
        RETURNING grievanceid;
    ";

    await using var insertCommand = new NpgsqlCommand(insertSql, connection);

    insertCommand.Parameters.AddWithValue("userID", userID);
    insertCommand.Parameters.AddWithValue("subject", request.Subject);
    insertCommand.Parameters.AddWithValue("description", request.Description);

    int grievanceID = (int)(await insertCommand.ExecuteScalarAsync())!;

    return Results.Ok(new
    {
        success = true,
        message = "Grievance submitted successfully.",
        grievanceID,
        status = "Pending"
    });
});
// EMPLOYEE: View submitted grievances
app.MapGet("/api/employees/{userID:int}/grievances", async (
    int userID,
    NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        SELECT grievanceid, subject, description, status,
               submitted_at, resolved_at
        FROM grievances
        WHERE userid = @userID
        ORDER BY submitted_at DESC;
    ";

    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("userID", userID);

    await using var reader = await command.ExecuteReaderAsync();

    var grievances = new List<object>();

    while (await reader.ReadAsync())
    {
        grievances.Add(new
        {
            grievanceID = reader.GetInt32(0),
            subject = reader.GetString(1),
            description = reader.GetString(2),
            status = reader.GetString(3),
            submittedAt = reader.GetDateTime(4),
            resolvedAt = reader.IsDBNull(5)
                ? (DateTime?)null
                : reader.GetDateTime(5)
        });
    }

    return Results.Ok(grievances);
});
// ADMIN: View all grievances
app.MapGet("/api/admin/grievances", async (
    NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        SELECT g.grievanceid, g.userid, u.username,
               g.subject, g.description, g.status,
               g.submitted_at, g.resolved_at
        FROM grievances g
        JOIN users u ON g.userid = u.userid
        ORDER BY g.submitted_at DESC;
    ";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var grievances = new List<object>();

    while (await reader.ReadAsync())
    {
        grievances.Add(new
        {
            grievanceID = reader.GetInt32(0),
            userID = reader.GetInt32(1),
            username = reader.GetString(2),
            subject = reader.GetString(3),
            description = reader.GetString(4),
            status = reader.GetString(5),
            submittedAt = reader.GetDateTime(6),
            resolvedAt = reader.IsDBNull(7)
                ? (DateTime?)null
                : reader.GetDateTime(7)
        });
    }

    return Results.Ok(grievances);
});
// ADMIN: Update grievance status
app.MapPut("/api/admin/grievances/{grievanceID:int}", async (
    int grievanceID,
    GrievanceStatusUpdate request,
    NpgsqlDataSource dataSource) =>
{
    if (request.Status != "In Progress" &&
        request.Status != "Resolved" &&
        request.Status != "Rejected")
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Invalid grievance status."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        UPDATE grievances
        SET status = @status,
            resolved_at = CASE
                WHEN @status = 'Resolved' THEN CURRENT_TIMESTAMP
                ELSE NULL
            END
        WHERE grievanceid = @grievanceID
        RETURNING grievanceid;
    ";

    await using var command = new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("status", request.Status);
    command.Parameters.AddWithValue("grievanceID", grievanceID);

    var result = await command.ExecuteScalarAsync();

    if (result == null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Grievance not found."
        });
    }

    return Results.Ok(new
    {
        success = true,
        message = "Grievance status updated successfully.",
        grievanceID,
        status = request.Status
    });
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
// ADMIN: Generate payroll and create payslips
app.MapPost("/api/admin/payroll/generate", async (
    PayrollGenerateRequest request,
    NpgsqlDataSource dataSource) =>
{
    if (request.EndDate < request.StartDate)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "End date cannot be before start date."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();

    // Retrieve payroll settings
    const string settingsSql = @"
        SELECT payfrequency, taxdeductionrate
        FROM payroll_settings
        ORDER BY settingid
        LIMIT 1;
    ";

    await using var settingsCommand =
        new NpgsqlCommand(settingsSql, connection);

    await using var settingsReader =
        await settingsCommand.ExecuteReaderAsync();

    if (!await settingsReader.ReadAsync())
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Payroll settings have not been configured."
        });
    }

    string payFrequency = settingsReader.GetString(0);
    decimal taxRate = settingsReader.GetDecimal(1);

    await settingsReader.CloseAsync();

    // Validate pay period
    int days = request.EndDate.DayNumber -
               request.StartDate.DayNumber + 1;

    if (payFrequency == "Fortnightly" && days != 14)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Fortnightly payroll must cover exactly 14 days."
        });
    }

    if (payFrequency == "Monthly" &&
        (request.StartDate.Day != 1 ||
         request.StartDate.Month != request.EndDate.Month ||
         request.StartDate.Year != request.EndDate.Year ||
         request.EndDate.Day !=
         DateTime.DaysInMonth(
             request.EndDate.Year,
             request.EndDate.Month)))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Monthly payroll must cover a full calendar month."
        });
    }

    // Retrieve employees
    const string employeeSql = @"
        SELECT u.userid, e.salary
        FROM users u
        JOIN employees e ON u.userid = e.userid
        WHERE u.role = 'Employee';
    ";

    await using var employeeCommand =
        new NpgsqlCommand(employeeSql, connection);

    await using var employeeReader =
        await employeeCommand.ExecuteReaderAsync();

    var employees = new List<(int UserID, decimal Salary)>();

    while (await employeeReader.ReadAsync())
    {
        employees.Add((
            employeeReader.GetInt32(0),
            employeeReader.GetDecimal(1)
        ));
    }

    await employeeReader.CloseAsync();

    if (employees.Count == 0)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "No employees found for payroll."
        });
    }
// Check for existing payroll records for this period
const string duplicateSql = @"
    SELECT COUNT(*)
    FROM payslips
    WHERE payperiod_start = @startDate
    AND payperiod_end = @endDate;
";

await using var duplicateCommand =
    new NpgsqlCommand(duplicateSql, connection);

duplicateCommand.Parameters.AddWithValue(
    "startDate", request.StartDate
);

duplicateCommand.Parameters.AddWithValue(
    "endDate", request.EndDate
);

long existingPayroll =
    (long)(await duplicateCommand.ExecuteScalarAsync())!;

if (existingPayroll > 0)
{
    return Results.BadRequest(new
    {
        success = false,
        message = "Payroll has already been generated for this period."
    });
}
    // Calculate payroll
    decimal divisor = payFrequency == "Monthly" ? 12 : 26;

    var payrollResults = new List<object>();

    foreach (var employee in employees)
    {
        decimal grossSalary = Math.Round(
            employee.Salary / divisor, 2
        );

        decimal taxDeductions = Math.Round(
            grossSalary * taxRate / 100, 2
        );

        decimal netSalary = grossSalary - taxDeductions;

        const string insertSql = @"
            INSERT INTO payslips
                (userid, payperiod_start, payperiod_end,
                 grosssalary, taxdeductions, otherdeductions,
                 netsalary, paymentdate)
            VALUES
                (@userID, @startDate, @endDate,
                 @gross, @tax, 0,
                 @net, @endDate)
            RETURNING payslipid;
        ";

        await using var insertCommand =
            new NpgsqlCommand(insertSql, connection);

        insertCommand.Parameters.AddWithValue(
            "userID", employee.UserID
        );

        insertCommand.Parameters.AddWithValue(
            "startDate", request.StartDate
        );

        insertCommand.Parameters.AddWithValue(
            "endDate", request.EndDate
        );

        insertCommand.Parameters.AddWithValue("gross", grossSalary);
        insertCommand.Parameters.AddWithValue("tax", taxDeductions);
        insertCommand.Parameters.AddWithValue("net", netSalary);

        int payslipID =
            (int)(await insertCommand.ExecuteScalarAsync())!;

        payrollResults.Add(new
        {
            payslipID,
            userID = employee.UserID,
            grossSalary,
            taxDeductions,
            netSalary
        });
    }

    return Results.Ok(new
    {
        success = true,
        message = "Payroll generated successfully.",
        payFrequency,
        taxRate,
        startDate = request.StartDate,
        endDate = request.EndDate,
        employeesProcessed = payrollResults.Count,
        payslips = payrollResults
    });
});
// ADMIN: Update payroll settings
app.MapPut("/api/admin/payroll/settings", async (
    PayrollSettingsUpdate request,
    NpgsqlDataSource dataSource) =>
{
    if (request.PayFrequency != "Fortnightly" &&
        request.PayFrequency != "Monthly")
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Pay frequency must be Fortnightly or Monthly."
        });
    }

    if (request.TaxDeductionRate < 0 ||
        request.TaxDeductionRate > 100)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Deduction rate must be between 0 and 100."
        });
    }

    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        UPDATE payroll_settings
        SET payfrequency = @frequency,
            taxdeductionrate = @rate,
            updated_at = CURRENT_TIMESTAMP
        WHERE settingid = (
            SELECT settingid
            FROM payroll_settings
            ORDER BY settingid
            LIMIT 1
        )
        RETURNING settingid;
    ";

    await using var command = new NpgsqlCommand(sql, connection);

    command.Parameters.AddWithValue("frequency", request.PayFrequency);
    command.Parameters.AddWithValue("rate", request.TaxDeductionRate);

    var result = await command.ExecuteScalarAsync();

    if (result == null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Payroll settings not found."
        });
    }

    return Results.Ok(new
    {
        success = true,
        message = "Payroll settings updated successfully.",
        payFrequency = request.PayFrequency,
        taxDeductionRate = request.TaxDeductionRate
    });
});
// ADMIN: View payroll reports
app.MapGet("/api/admin/payroll/reports", async (
    NpgsqlDataSource dataSource) =>
{
    await using var connection = await dataSource.OpenConnectionAsync();

    const string sql = @"
        SELECT p.payslipid, p.userid, u.username,
               p.payperiod_start, p.payperiod_end,
               p.grosssalary, p.taxdeductions,
               p.otherdeductions, p.netsalary,
               p.paymentdate
        FROM payslips p
        JOIN users u ON p.userid = u.userid
        ORDER BY p.paymentdate DESC;
    ";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var reports = new List<object>();

    while (await reader.ReadAsync())
    {
        reports.Add(new
        {
            payslipID = reader.GetInt32(0),
            userID = reader.GetInt32(1),
            username = reader.GetString(2),
            payPeriodStart = reader.GetDateTime(3),
            payPeriodEnd = reader.GetDateTime(4),
            grossSalary = reader.GetDecimal(5),
            taxDeductions = reader.GetDecimal(6),
            otherDeductions = reader.GetDecimal(7),
            netSalary = reader.GetDecimal(8),
            paymentDate = reader.GetDateTime(9)
        });
    }

    return Results.Ok(reports);
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
public record EmployeeInformationUpdateRequest(
    string Username,
    string Password,
    string? Email
);
public record LeaveRequestCreate(
    string LeaveType,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Reason
);
public record LeaveRequestUpdate(string Status);
public record HolidayCreateRequest(
    string HolidayName,
    DateOnly HolidayDate,
    string? Description
);
public record HolidayUpdateRequest(
    string HolidayName,
    DateOnly HolidayDate,
    string? Description
);
public record GrievanceCreateRequest(
    string Subject,
    string Description
);
public record GrievanceStatusUpdate(string Status);
public record PayrollSettingsUpdate(
    string PayFrequency,
    decimal TaxDeductionRate
);
public record PayrollGenerateRequest(
    DateOnly StartDate,
    DateOnly EndDate
);
