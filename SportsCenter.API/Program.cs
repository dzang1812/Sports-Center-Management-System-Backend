using SportsCenter.API;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi.Models;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Auth;
using SportsCenter.Services.Audit;
using SportsCenter.Services.Email;
using SportsCenter.Services.Profile;
using SportsCenter.Services.Users;
using SportsCenter.Services.Scheduling;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter())); // enums as strings ("Coach")
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = context => new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(new
    {
        errorCode = "VALIDATION_ERROR",
        message = "Dữ liệu đầu vào không hợp lệ.",
        errors = context.ModelState.Where(x => x.Value?.Errors.Count > 0)
            .ToDictionary(x => x.Key, x => x.Value!.Errors.Select(e => e.ErrorMessage).ToArray()),
    }));
builder.Services.AddDbContext<SportsCenter.Repository.Data.AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddOptions<SchedulingOptions>()
    .Bind(builder.Configuration.GetSection(SchedulingOptions.SectionName))
    .Validate(x => x.ClassCancellationCutoffHours >= 0 && x.PtCancellationCutoffHours >= 0 &&
        x.ClassBookingClosesMinutesBefore >= 0 && x.PtBookingClosesMinutesBefore >= 0 &&
        x.ClassBookingOpensDaysBefore is null or >= 0 && x.PtBookingOpensDaysBefore is null or >= 0,
        "Scheduling options must be non-negative.")
    .ValidateOnStart();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>();
if (string.IsNullOrWhiteSpace(jwt?.Key))
    throw new InvalidOperationException(
        "Missing Jwt:Key. Set it with: dotnet user-secrets set \"Jwt:Key\" \"<a long random string>\"");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserAdminRepository, UserAdminRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.SectionName));
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IMeService, MeService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IAppClock, VietnamClock>();
builder.Services.AddScoped<ISchedulingTransaction, SchedulingTransaction>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<IClassScheduleService, ClassScheduleService>();
builder.Services.AddScoped<IClassBookingService, ClassBookingService>();
builder.Services.AddScoped<IPtService, PtService>();
builder.Services.AddScoped<IScheduleAttendanceService, ScheduleAttendanceService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.OperationFilter<SchedulingOpenApiOperationFilter>();
    // Adds the "Authorize" button: paste the JWT from /api/auth/login (no "Bearer " prefix).
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SportsCenter.Repository.Data.AppDbContext>();
    await db.Database.MigrateAsync();
    await DemoSeeder.SeedAsync(db);
}

app.UseHttpsRedirection();

app.UseMiddleware<SchedulingExceptionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
