using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Domain;
using static Oficina.Execution.Infrastructure.Persistence.ItensDynamoDb;
using static Oficina.Execution.Infrastructure.Persistence.TabelaExecucoes;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>
/// Repositório da execução no DynamoDB. A gravação é um <c>TransactWriteItems</c> com:
/// o registro da mensagem processada (<c>attribute_not_exists</c>), o META (condição na versão),
/// os apontamentos novos e o outbox. Ou entra tudo, ou nada.
/// </summary>
internal sealed class ExecucaoRepositorioDynamoDb : IExecucaoRepositorio
{
    private const string FalhaDeCondicao = "ConditionalCheckFailed";

    private readonly IAmazonDynamoDB _dynamo;
    private readonly TimeProvider _relogio;
    private readonly string _tabela;

    public ExecucaoRepositorioDynamoDb(IAmazonDynamoDB dynamo, IOptions<DynamoDbOptions> opcoes, TimeProvider relogio)
    {
        _dynamo = dynamo;
        _relogio = relogio;
        _tabela = opcoes.Value.TableName;
    }

    public async Task<Execucao?> ObterAsync(int osId, CancellationToken cancellationToken = default)
    {
        // Um Query pela PK traz o META e os apontamentos. Leitura consistente: é a base
        // de uma gravação condicional logo em seguida.
        Dictionary<string, AttributeValue>? meta = null;
        var apontamentos = new List<Apontamento>();
        Dictionary<string, AttributeValue>? inicio = null;

        do
        {
            var resposta = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName = _tabela,
                KeyConditionExpression = "#pk = :pk",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = PK },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(ChaveExecucao(osId)) },
                ConsistentRead = true,
                ExclusiveStartKey = inicio
            }, cancellationToken);

            foreach (var item in resposta.Items ?? [])
            {
                var sk = item[SK].S;
                if (sk == Meta)
                    meta = item;
                else if (sk.StartsWith(PrefixoApontamento, StringComparison.Ordinal))
                    apontamentos.Add(ParaApontamento(item));
            }

            inicio = resposta.LastEvaluatedKey is { Count: > 0 } proxima ? proxima : null;
        }
        while (inicio is not null);

        return meta is null ? null : Execucao.Restaurar(ParaEstado(meta), apontamentos);
    }

    public async Task<IReadOnlyList<EstadoExecucao>> ListarFilaAsync(EtapaExecucao etapa, int limite, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limite);

        var resposta = await _dynamo.QueryAsync(new QueryRequest
        {
            TableName = _tabela,
            IndexName = IndiceFila,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = Gsi1Pk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = S(ChaveFila(etapa)) },
            ScanIndexForward = true,
            Limit = limite
        }, cancellationToken);

        return (resposta.Items ?? []).Select(ParaEstado).ToList();
    }

    public async Task<bool> MensagemJaProcessadaAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var resposta = await _dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _tabela,
            Key = ChaveMensagemProcessada(messageId),
            ProjectionExpression = "#pk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = PK },
            ConsistentRead = true
        }, cancellationToken);

        return resposta.Item is { Count: > 0 };
    }

    public async Task<ResultadoGravacao> SalvarAsync(
        Execucao? execucao,
        IReadOnlyList<object> mensagens,
        Guid? mensagemProcessada,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mensagens);

        var agora = _relogio.GetUtcNow().UtcDateTime;
        var itens = new List<TransactWriteItem>();
        int? posicaoMensagem = null;
        int? posicaoExecucao = null;

        if (mensagemProcessada is { } messageId)
        {
            posicaoMensagem = itens.Count;
            itens.Add(Inserir(ItemMensagemProcessada(messageId, agora)));
        }

        if (execucao is not null)
        {
            posicaoExecucao = itens.Count;
            itens.Add(GravarMeta(execucao));
            itens.AddRange(execucao.NovosApontamentos.Select(a =>
                new TransactWriteItem { Put = new Put { TableName = _tabela, Item = ItemApontamento(execucao.OsId, a) } }));
        }

        itens.AddRange(mensagens.Select(m =>
            new TransactWriteItem { Put = new Put { TableName = _tabela, Item = ItemOutbox(Guid.NewGuid(), m, agora) } }));

        if (itens.Count == 0)
            return ResultadoGravacao.Gravada;

        try
        {
            await _dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = itens }, cancellationToken);
        }
        catch (TransactionCanceledException ex)
        {
            var motivos = ex.CancellationReasons ?? [];

            if (FalhouACondicao(motivos, posicaoMensagem))
                return ResultadoGravacao.MensagemDuplicada;

            if (FalhouACondicao(motivos, posicaoExecucao))
                throw new ConflitoDeVersaoException(
                    $"A execução da OS {execucao!.NumeroOS} mudou desde a leitura (versão lida: {execucao.Version}).", ex);

            throw;
        }

        execucao?.MarcarComoGravada();
        return ResultadoGravacao.Gravada;
    }

    /// <summary>Execução nova não pode existir; a existente precisa estar na versão lida.</summary>
    private TransactWriteItem GravarMeta(Execucao execucao)
    {
        var put = new Put { TableName = _tabela, Item = ItemExecucao(execucao.Estado, execucao.Version + 1) };

        if (execucao.Version == 0)
        {
            put.ConditionExpression = "attribute_not_exists(#pk)";
            put.ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = PK };
        }
        else
        {
            put.ConditionExpression = "#v = :v";
            put.ExpressionAttributeNames = new Dictionary<string, string> { ["#v"] = "Version" };
            put.ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":v"] = N(execucao.Version) };
        }

        return new TransactWriteItem { Put = put };
    }

    private TransactWriteItem Inserir(Dictionary<string, AttributeValue> item) => new()
    {
        Put = new Put
        {
            TableName = _tabela,
            Item = item,
            ConditionExpression = "attribute_not_exists(#pk)",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = PK }
        }
    };

    private static bool FalhouACondicao(List<CancellationReason> motivos, int? posicao) =>
        posicao is { } p && p < motivos.Count && motivos[p].Code == FalhaDeCondicao;
}
