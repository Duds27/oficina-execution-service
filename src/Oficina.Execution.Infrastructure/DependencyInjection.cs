using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Oficina.Execution.Application;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Infrastructure.Messaging;
using Oficina.Execution.Infrastructure.Persistence;

namespace Oficina.Execution.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registra persistência, mensageria e integrações externas.
    /// Cada dependência adiciona aqui o seu health check com a tag "ready",
    /// que passa a compor o <c>/health/ready</c>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<FaultInjectionOptions>(configuration.GetSection(FaultInjectionOptions.Secao));

        // A persistência primeiro: a tabela fica pronta antes de o bus começar a consumir.
        services.AddPersistencia(configuration);
        services.AddMensageria(configuration);
        return services;
    }

    private static IServiceCollection AddPersistencia(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DynamoDbOptions>(configuration.GetSection(DynamoDbOptions.Secao));

        services.AddSingleton<IAmazonDynamoDB>(sp =>
        {
            var opcoes = sp.GetRequiredService<IOptions<DynamoDbOptions>>().Value;

            // Na AWS: região e credenciais do ambiente (Pod Identity no EKS; IRSA também funciona, via AWSSDK.SecurityToken).
            if (string.IsNullOrWhiteSpace(opcoes.ServiceUrl))
                return new AmazonDynamoDBClient();

            // O DynamoDB Local não valida credenciais, mas o SDK exige alguma para assinar.
            return new AmazonDynamoDBClient(
                new BasicAWSCredentials("local", "local"),
                new AmazonDynamoDBConfig { ServiceURL = opcoes.ServiceUrl, AuthenticationRegion = opcoes.Region });
        });

        services.AddSingleton<IExecucaoRepositorio, ExecucaoRepositorioDynamoDb>();
        services.AddSingleton<OutboxDynamoDb>();

        // Antes de qualquer hosted service que use a tabela.
        services.AddHostedService<CriadorTabelaDesenvolvimento>();

        // Gauge execucao.fila.tamanho (Etapa 5).
        services.Configure<MetricasOptions>(configuration.GetSection(MetricasOptions.Secao));
        services.AddHostedService<MonitorFila>();

        services.AddHealthChecks().AddCheck<DynamoDbHealthCheck>("dynamodb", tags: ["ready"]);
        return services;
    }
}
