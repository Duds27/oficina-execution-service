using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using static Oficina.Execution.Infrastructure.Persistence.ItensDynamoDb;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>
/// Readiness do DynamoDB: um <c>GetItem</c> numa chave que não existe. Usa só uma permissão
/// que a role do pod já tem (sem <c>DescribeTable</c>) e falha se a tabela não existir.
/// </summary>
internal sealed class DynamoDbHealthCheck : IHealthCheck
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tabela;

    public DynamoDbHealthCheck(IAmazonDynamoDB dynamo, IOptions<DynamoDbOptions> opcoes)
    {
        _dynamo = dynamo;
        _tabela = opcoes.Value.TableName;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _dynamo.GetItemAsync(new GetItemRequest
            {
                TableName = _tabela,
                Key = new Dictionary<string, AttributeValue>
                {
                    [TabelaExecucoes.PK] = S("HEALTH"),
                    [TabelaExecucoes.SK] = S("HEALTH")
                }
            }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is AmazonDynamoDBException or System.Net.Http.HttpRequestException or Amazon.Runtime.AmazonServiceException)
        {
            return HealthCheckResult.Unhealthy($"DynamoDB indisponível (tabela {_tabela}).", ex);
        }
    }
}
