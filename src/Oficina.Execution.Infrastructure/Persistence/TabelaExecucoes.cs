using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Oficina.Execution.Domain;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>
/// Tabela única (<i>single-table</i>) do execution-service. Itens:
/// <list type="bullet">
///   <item>Execução: <c>EXEC#{osId}</c> / <c>META</c>, na fila pelo GSI1 (<c>FILA#{etapa}</c>).</item>
///   <item>Apontamento: <c>EXEC#{osId}</c> / <c>EVT#{data}#{id}</c>.</item>
///   <item>Mensagem processada: <c>MSG#{messageId}</c> / <c>MSG</c>, com TTL em <c>ExpiraEm</c>.</item>
///   <item>Outbox: <c>OUTBOX#{id}</c> / <c>OUTBOX</c>, listado pelo GSI2.</item>
/// </list>
/// A mesma definição está no Terraform (<c>infra/</c>); aqui ela só é usada no ambiente Development e nos testes.
/// </summary>
public static class TabelaExecucoes
{
    public const string PK = "PK";
    public const string SK = "SK";
    public const string Gsi1Pk = "GSI1PK";
    public const string Gsi1Sk = "GSI1SK";
    public const string Gsi2Pk = "GSI2PK";
    public const string Gsi2Sk = "GSI2SK";
    public const string IndiceFila = "GSI1";
    public const string IndiceOutbox = "GSI2";
    public const string AtributoTtl = "ExpiraEm";

    internal const string Meta = "META";
    internal const string PrefixoApontamento = "EVT#";
    internal const string Outbox = "OUTBOX";
    internal const string Mensagem = "MSG";

    internal static string ChaveExecucao(int osId) => $"EXEC#{osId}";
    internal static string ChaveFila(EtapaExecucao etapa) => $"FILA#{etapa}";
    internal static string OrdemNaFila(DateTime desde, int osId) => $"{Data(desde)}#{osId:D10}";
    internal static string ChaveApontamento(Apontamento a) => $"{PrefixoApontamento}{Data(a.Em)}#{a.Id}";
    internal static string ChaveMensagem(Guid messageId) => $"MSG#{messageId:N}";
    internal static string ChaveOutbox(Guid id) => $"OUTBOX#{id:N}";

    /// <summary>ISO 8601 em UTC com largura fixa: a ordem do texto é a ordem do tempo.</summary>
    internal static string Data(DateTime valor) =>
        Utc(valor).ToString("O", CultureInfo.InvariantCulture);

    internal static DateTime Utc(DateTime valor) => valor.Kind switch
    {
        DateTimeKind.Utc => valor,
        DateTimeKind.Local => valor.ToUniversalTime(),
        _ => DateTime.SpecifyKind(valor, DateTimeKind.Utc)
    };

    public static CreateTableRequest Definicao(string nome) => new()
    {
        TableName = nome,
        BillingMode = BillingMode.PAY_PER_REQUEST,
        AttributeDefinitions =
        [
            new AttributeDefinition(PK, ScalarAttributeType.S),
            new AttributeDefinition(SK, ScalarAttributeType.S),
            new AttributeDefinition(Gsi1Pk, ScalarAttributeType.S),
            new AttributeDefinition(Gsi1Sk, ScalarAttributeType.S),
            new AttributeDefinition(Gsi2Pk, ScalarAttributeType.S),
            new AttributeDefinition(Gsi2Sk, ScalarAttributeType.S)
        ],
        KeySchema = [new KeySchemaElement(PK, KeyType.HASH), new KeySchemaElement(SK, KeyType.RANGE)],
        GlobalSecondaryIndexes =
        [
            Indice(IndiceFila, Gsi1Pk, Gsi1Sk),
            Indice(IndiceOutbox, Gsi2Pk, Gsi2Sk)
        ]
    };

    /// <summary>Cria a tabela (e liga o TTL) se ela ainda não existir. Devolve se criou.</summary>
    public static async Task<bool> CriarSeNaoExistirAsync(IAmazonDynamoDB dynamo, string nome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dynamo);

        try
        {
            await dynamo.DescribeTableAsync(nome, cancellationToken);
            return false;
        }
        catch (ResourceNotFoundException)
        {
            // Segue para a criação.
        }

        try
        {
            await dynamo.CreateTableAsync(Definicao(nome), cancellationToken);
        }
        catch (ResourceInUseException)
        {
            return false; // Outra instância criou ao mesmo tempo.
        }

        await dynamo.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = nome,
            TimeToLiveSpecification = new TimeToLiveSpecification { AttributeName = AtributoTtl, Enabled = true }
        }, cancellationToken);
        return true;
    }

    private static GlobalSecondaryIndex Indice(string nome, string pk, string sk) => new()
    {
        IndexName = nome,
        KeySchema = [new KeySchemaElement(pk, KeyType.HASH), new KeySchemaElement(sk, KeyType.RANGE)],
        Projection = new Projection { ProjectionType = ProjectionType.ALL }
    };
}
