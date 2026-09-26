using Microsoft.EntityFrameworkCore;
using Serilog;
using Turning.API.Extensions;
using Turning.API.Middleware;
using Turning.Application.DependencyInjection;
using Turning.Infrastructure.DependencyInjection;
using Turning.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Configurar Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// Agregar servicios
builder.Services
    .AddApplicationServices()
    .AddInfrastructureServices(builder.Configuration);

builder.Services.AddCorsConfiguration(builder.Configuration);

var startupLogger = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger("Turning.API.Jwt");
builder.Services.AddJwtAuthentication(builder.Configuration, builder.Environment, startupLogger);

var aiLogger = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger("Turning.API.Ai");
builder.Services.AddAiTextGeneration(builder.Configuration, builder.Environment, aiLogger);

builder.Services.AddAuthorization();

// Sonda de vida para el contenedor / Azure.
builder.Services.AddHealthChecks();

// En contenedor el TLS termina en el proxy de entrada (Azure): sin esto la API
// ve todo como http y UseHttpsRedirection decide mal. El proxy no tiene IP fija,
// por eso se limpian las listas de proxies conocidos.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Agregar controllers y Swagger
// El filtro de propietario va global: las rutas anidadas bajo
// /api/sessions/{sessionId}/... nacieron sin validarlo, y hacerlo opt-in
// repetiria el problema con el proximo controller que se agregue.
builder.Services.AddControllers(options =>
{
    options.Filters.Add<Turning.API.Filters.SessionOwnershipFilter>();
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Turning API",
        Version = "v1",
        Description = "API del experimento de interacciones empaticas humano-IA. "
            + "Las rutas de sesion aplican aislamiento por propietario: una sesion ajena "
            + "responde 404, igual que una inexistente, para no revelar por enumeracion "
            + "que el identificador existe."
    });

    // Los <summary> de controllers y DTOs se muestran en Swagger UI.
    var xmlApi = Path.Combine(AppContext.BaseDirectory, "turning.API.xml");
    if (File.Exists(xmlApi)) options.IncludeXmlComments(xmlApi, includeControllerXmlComments: true);

    var xmlApplication = Path.Combine(AppContext.BaseDirectory, "turning.Application.xml");
    if (File.Exists(xmlApplication)) options.IncludeXmlComments(xmlApplication);

    // Sin esto no se puede probar un endpoint autenticado desde Swagger UI.
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Token JWT obtenido de POST /api/auth/login. Se envia como: Bearer {token}"
    });

    options.AddSecurityRequirement(document => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document),
            new List<string>()
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<TurningDbContext>();
    dbContext.Database.Migrate();
    await Turning.Infrastructure.Persistence.TurningDbSeeder.SeedAsync(dbContext);
}

// Configurar middleware
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Turning API v1");
    });
}

app.UseExceptionHandling();

if (app.Environment.IsDevelopment())
{
    // En desarrollo, /api no se redirige a HTTPS. Un preflight OPTIONS no sigue
    // redirecciones: si se le responde 307, el navegador aborta la peticion y
    // reporta un error de CORS opaco, sin que la API registre nada. Fuera de
    // Development se mantiene la redireccion para todo.
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api"),
        branch => branch.UseHttpsRedirection());

    foreach (var origen in Turning.API.Extensions.ServiceExtensions.GetConfiguredCorsOrigins(builder.Configuration))
    {
        app.Logger.LogInformation("CORS: origen permitido {Origen}", origen);
    }
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowSpecific");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
