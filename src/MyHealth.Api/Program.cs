using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MyHealth.Api.Auth;
using MyHealth.Api.Data;
using MyHealth.Api.Features.Evaluation;
using MyHealth.Api.Features.Insights;
using MyHealth.Api.Features.Metrics;
using MyHealth.Api.Features.Sleep;
using MyHealth.Api.Features.User;
using MyHealth.Api.Features.Workouts;

var builder = WebApplication.CreateBuilder(args);

// Не переименовывать стандартные JWT-claim'ы (оставляем "sub" как есть).
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

// --- Конфигурация ---
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.SectionName));
var jwt = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
          ?? new JwtSettings();

// --- База данных (PostgreSQL) ---
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// --- Аутентификация / авторизация ---
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<TokenService>();

// JSON: enum'ы как строки (совпадает с Flutter-клиентом).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS: в dev разрешаем любой источник (Flutter web на произвольном порту).
// В проде заменить на белый список доменов клиента.
builder.Services.AddCors(o => o.AddPolicy("dev", p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// За reverse-proxy (Nginx) — доверяем заголовкам X-Forwarded-* (схема/IP клиента).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// Применяем миграции при старте (и в dev, и в проде — одно-инстансное развёртывание).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("dev");
}

// TLS терминируется на Nginx; редирект http→https делает Nginx, поэтому
// UseHttpsRedirection в приложении не нужен (иначе возможны петли за прокси).
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }))
    .WithTags("System");

app.MapAuthEndpoints();
app.MapMetricEndpoints();
app.MapEvaluationEndpoints();
app.MapWorkoutEndpoints();
app.MapSleepEndpoints();
app.MapUserEndpoints();
app.MapInsightEndpoints();

app.Run();
