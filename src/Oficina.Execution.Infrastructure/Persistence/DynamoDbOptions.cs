namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>Seção <c>DynamoDb</c> (variáveis <c>DynamoDb__*</c>).</summary>
public sealed class DynamoDbOptions
{
    public const string Secao = "DynamoDb";

    public string TableName { get; set; } = "oficina-execucoes";

    /// <summary>
    /// Endereço do DynamoDB Local (docker-compose e testes). Vazio na AWS: o SDK usa a região
    /// e as credenciais do ambiente (IRSA no EKS).
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Região usada para assinar as chamadas ao DynamoDB Local.</summary>
    public string Region { get; set; } = "us-east-1";
}
