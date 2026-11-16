using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Oficina.Execution.Infrastructure.Persistence;
using Testcontainers.DynamoDb;

namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>
/// Um DynamoDB Local (mesma imagem do docker-compose) para a coleção inteira.
/// Cada teste cria a sua tabela, para não depender da ordem nem dos dados dos outros.
/// </summary>
public sealed class DynamoDbFixture : IAsyncLifetime
{
    private readonly DynamoDbContainer _container = new DynamoDbBuilder()
        .WithImage("amazon/dynamodb-local:2.6.1")
        .Build();

    public string ServiceUrl => _container.GetConnectionString();

    public IAmazonDynamoDB Cliente { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Cliente = new AmazonDynamoDBClient(
            new BasicAWSCredentials("local", "local"),
            new AmazonDynamoDBConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "us-east-1" });
    }

    public async Task<string> NovaTabelaAsync()
    {
        var nome = $"execucoes-{Guid.NewGuid():N}";
        await TabelaExecucoes.CriarSeNaoExistirAsync(Cliente, nome, CancellationToken.None);
        return nome;
    }

    public async Task DisposeAsync()
    {
        Cliente?.Dispose();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoDynamoDb : ICollectionFixture<DynamoDbFixture>
{
    public const string Nome = "DynamoDB Local";
}
