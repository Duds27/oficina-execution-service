using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using static Oficina.Execution.Infrastructure.Persistence.ItensDynamoDb;
using static Oficina.Execution.Infrastructure.Persistence.TabelaExecucoes;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>Leitura e limpeza do outbox, para o publicador.</summary>
internal sealed class OutboxDynamoDb
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tabela;

    public OutboxDynamoDb(IAmazonDynamoDB dynamo, IOptions<DynamoDbOptions> opcoes)
    {
        _dynamo = dynamo;
        _tabela = opcoes.Value.TableName;
    }

    /// <summary>Os mais antigos primeiro (GSI2, ordenado pela data de criação).</summary>
    public async Task<IReadOnlyList<RegistroOutbox>> ListarPendentesAsync(int limite, CancellationToken cancellationToken)
    {
        var resposta = await _dynamo.QueryAsync(new QueryRequest
        {
            TableName = _tabela,
            IndexName = IndiceOutbox,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Gsi2Pk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(TabelaExecucoes.Outbox) },
            ScanIndexForward = true,
            Limit = limite
        }, cancellationToken);

        return (resposta.Items ?? []).Select(ParaRegistroOutbox).ToList();
    }

    public Task RemoverAsync(Guid id, CancellationToken cancellationToken) =>
        _dynamo.DeleteItemAsync(new DeleteItemRequest { TableName = _tabela, Key = ChaveRegistroOutbox(id) }, cancellationToken);
}
