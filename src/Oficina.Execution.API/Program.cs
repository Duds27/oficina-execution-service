using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Oficina.Execution.API.Middleware;
using Oficina.Execution.API.Security;
using Oficina.Execution.Application;
using Oficina.Execution.Infrastructure;
using Oficina.Execution.API.Observabilidade;
using Serilog;
using Serilog.Formatting.Compact;

const string ServiceName = "execution-service";

// Logs estruturados em JSON (CompactJsonFormatter) no stdout, coletados pelo
// nri-bundle no EKS. Mesmo formato do os-service.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", ServiceName)
        // trace.id/span.id em todo log e, com endpoint OTLP, o envio dos logs (Etapa 5).
        .ComTelemetria(ctx.Configuration, ctx.HostingEnvironment)
        .WriteTo.Console(new CompactJsonFormatter()));

    // OpenTelemetry: traces e métricas, exportados por OTLP quando OTEL_EXPORTER_OTLP_ENDPOINT existe.
    builder.Services.AddObservabilidade(builder.Configuration, builder.Environment);

    // Clean Architecture: composição das camadas
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // JWT emitido pela Lambda de autenticação: mesma chave, issuer e audience do os-service.
    var jwt = builder.Configuration.GetSection("Jwt");
    var jwtKey = jwt["Key"] ?? throw new InvalidOperationException("Jwt:Key não configurado.");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtKey)),
                ValidateIssuer = true,
                ValidIssuer = jwt["Issuer"],
                ValidateAudience = true,
                ValidAudience = jwt["Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });
    builder.Services.AddAuthorization(options =>
        options.AddPolicy(Politicas.Oficina, p => p.RequireRole(Politicas.PapeisDaOficina)));

    builder.Services.AddControllers();

    // Liveness não tem checks. Readiness usa os checks com a tag "ready",
    // registrados pela Infrastructure (DynamoDB e RabbitMQ).
    builder.Services.AddHealthChecks();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "Oficina Execution API", Version = "v1" });

        foreach (var xml in Directory.GetFiles(AppContext.BaseDirectory, "Oficina.Execution*.xml"))
            c.IncludeXmlComments(xml);

        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "Cole apenas o token JWT (sem o prefixo 'Bearer ').",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT"
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    });

    var app = builder.Build();

    // Falha simulada ligada muda o comportamento da saga inteira: fica explícito no log.
    var falhas = app.Services.GetRequiredService<IOptions<FaultInjectionOptions>>().Value;
    if (falhas.RejeitarExecucao)
        Log.Warning("FaultInjection:RejeitarExecucao ligado: todo IniciarExecucao será rejeitado com \"{Motivo}\"", FaultInjectionOptions.MotivoRejeicaoSimulada);
    else
        Log.Information("FaultInjection:RejeitarExecucao desligado");

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // Correlação antes do log de requisição, para o CorrelationId aparecer no evento de conclusão.
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
        .AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") })
        .AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Aplicação encerrada inesperadamente");
}
finally
{
    Log.CloseAndFlush();
}

// Exposto para o WebApplicationFactory dos testes de integração.
public partial class Program { }
