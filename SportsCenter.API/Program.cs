using SportsCenter.API;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
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
using SportsCenter.Services.Membership;
using SportsCenter.Services.Payments;
using SportsCenter.Services.Media;
using CloudinaryDotNet;

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
        x.ClassCheckInOpensMinutesBefore >= 0 && x.ClassPresentGraceMinutes >= 0 &&
        x.ClassCheckInCodeLifetimeMinutes is >= 1 and <= 30 &&
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
builder.Services.AddSingleton<IFlow2TimeRules, Flow2TimeRulesState>();

// Closed by default (empty array = no cross-origin browser calls allowed). Set real origins per
// environment: appsettings.Development.json for local FE dev, env var Cors__AllowedOrigins__0 on the
// deployed server once the FE's production URL is known.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("Default", p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserAdminRepository, UserAdminRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.SectionName));
builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(c => c.BaseAddress = new Uri("https://api.resend.com/"));
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IMeService, MeService>();
builder.Services.AddScoped<ICoachProfileRepository, CoachProfileRepository>();
builder.Services.AddScoped<CoachProfileService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IAppClock, VietnamClock>();
builder.Services.AddScoped<ISchedulingTransaction, SchedulingTransaction>();
builder.Services.AddScoped<IScheduleUserRepository, ScheduleUserRepository>();
builder.Services.AddScoped<IScheduleConflictRepository, ScheduleConflictRepository>();
builder.Services.AddScoped<ISchedulingSubscriptionRepository, SchedulingSubscriptionRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<ICatalogRepository, CatalogRepository>();
builder.Services.AddScoped<IClassRepository, ClassRepository>();
builder.Services.AddScoped<IPtRepository, PtRepository>();
builder.Services.AddScoped<IAttendanceRepository, AttendanceRepository>();
builder.Services.AddScoped<ICommerceRepository, CommerceRepository>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<Flow2CommerceService>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<IClassScheduleService, ClassScheduleService>();
builder.Services.AddScoped<IClassBookingService, ClassBookingService>();
builder.Services.AddScoped<IPtService, PtService>();
builder.Services.AddScoped<IScheduleAttendanceService, ScheduleAttendanceService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

builder.Services.Configure<PayOsOptions>(builder.Configuration.GetSection(PayOsOptions.SectionName));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PayOsOptions>>().Value;
    if (string.IsNullOrWhiteSpace(o.ClientId) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.ChecksumKey))
        throw new InvalidOperationException("Missing PayOs:ClientId/ApiKey/ChecksumKey. Set them via dotnet user-secrets.");
    return new PayOS.PayOSClient(o.ClientId, o.ApiKey, o.ChecksumKey, null);
});
builder.Services.AddScoped<IMembershipRepository, MembershipRepository>();
builder.Services.AddScoped<IGymAccessRepository, GymAccessRepository>();
builder.Services.AddScoped<IMembershipPlanService, MembershipPlanService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IGymAccessService, GymAccessService>();
builder.Services.AddScoped<IMembershipReportService, MembershipReportService>();

builder.Services.Configure<CloudinaryOptions>(builder.Configuration.GetSection(CloudinaryOptions.SectionName));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudinaryOptions>>().Value;
    if (string.IsNullOrWhiteSpace(o.CloudName) || string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.ApiSecret))
        throw new InvalidOperationException("Missing Cloudinary:CloudName/ApiKey/ApiSecret. Set them via dotnet user-secrets.");
    return new Cloudinary(new Account(o.CloudName, o.ApiKey, o.ApiSecret));
});

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

// Behind nginx on EC2, Kestrel only ever sees plain HTTP from localhost; without this,
// UseHttpsRedirection() can't tell the original request was HTTPS and will redirect-loop.
// No-op in local dev (no proxy in front), so safe to always register.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

app.UseHttpsRedirection();

app.UseMiddleware<SchedulingExceptionMiddleware>();
app.UseCors("Default");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
