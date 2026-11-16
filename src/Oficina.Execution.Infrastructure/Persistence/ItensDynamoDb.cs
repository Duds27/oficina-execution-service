using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Oficina.Execution.Domain;
using static Oficina.Execution.Infrastructure.Persistence.TabelaExecucoes;

namespace Oficina.Execution.Infrastructure.Persistence;

/// <summary>Conversão entre o domínio e os itens da tabela (API de baixo nível do SDK).</summary>
internal static class ItensDynamoDb
{
    /// <summary>Dedup vale por 7 dias: bem mais que o retry e o redelivery de uma mensagem.</summary>
    internal static readonly TimeSpan RetencaoMensagemProcessada = TimeSpan.FromDays(7);

    internal static readonly JsonSerializerOptions JsonOutbox = new(JsonSerializerDefaults.Web);

    // ───────────── Execução (META) ─────────────

    internal static Dictionary<string, AttributeValue> ItemExecucao(EstadoExecucao e, int versao)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            [PK] = S(ChaveExecucao(e.OsId)),
            [SK] = S(Meta),
            ["Tipo"] = S("Execucao"),
            ["OsId"] = N(e.OsId),
            ["CorrelationId"] = S(e.CorrelationId.ToString()),
            ["NumeroOS"] = S(e.NumeroOS),
            ["Etapa"] = S(e.Etapa.ToString()),
            ["EtapaDesde"] = S(Data(e.EtapaDesde)),
            ["CriadaEm"] = S(Data(e.CriadaEm)),
            ["Version"] = N(versao),
            [Gsi1Pk] = S(ChaveFila(e.Etapa)),
            [Gsi1Sk] = S(OrdemNaFila(e.EtapaDesde, e.OsId))
        };

        if (e.Veiculo is { } v)
        {
            item["Veiculo"] = new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    ["Placa"] = S(v.Placa),
                    ["Marca"] = S(v.Marca),
                    ["Modelo"] = S(v.Modelo),
                    ["Ano"] = N(v.Ano)
                }
            };
        }

        // Lista vazia não é gravada: a leitura trata a ausência como lista vazia.
        if (e.Servicos.Count > 0)
        {
            item["Servicos"] = new AttributeValue
            {
                L = e.Servicos.Select(s => new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        ["Descricao"] = S(s.Descricao),
                        ["Quantidade"] = N(s.Quantidade)
                    }
                }).ToList()
            };
        }

        Opcional(item, "Descricao", e.Descricao);
        Opcional(item, "Mecanico", e.Mecanico);
        Opcional(item, "ObservacoesDiagnostico", e.ObservacoesDiagnostico);
        Opcional(item, "MotivoCancelamento", e.MotivoCancelamento);
        Opcional(item, "DiagnosticoIniciadoEm", e.DiagnosticoIniciadoEm);
        Opcional(item, "DiagnosticoConcluidoEm", e.DiagnosticoConcluidoEm);
        Opcional(item, "ExecucaoIniciadaEm", e.ExecucaoIniciadaEm);
        Opcional(item, "ReparoIniciadoEm", e.ReparoIniciadoEm);
        Opcional(item, "FinalizadaEm", e.FinalizadaEm);
        Opcional(item, "CanceladaEm", e.CanceladaEm);
        return item;
    }

    internal static EstadoExecucao ParaEstado(Dictionary<string, AttributeValue> item) => new()
    {
        OsId = Inteiro(item, "OsId"),
        CorrelationId = Guid.Parse(Texto(item, "CorrelationId")),
        NumeroOS = Texto(item, "NumeroOS"),
        Veiculo = item.TryGetValue("Veiculo", out var v) && v.M is { } m
            ? new Veiculo(Texto(m, "Placa"), Texto(m, "Marca"), Texto(m, "Modelo"), Inteiro(m, "Ano"))
            : null,
        Descricao = TextoOpcional(item, "Descricao"),
        Etapa = Enum.Parse<EtapaExecucao>(Texto(item, "Etapa")),
        EtapaDesde = DataObrigatoria(item, "EtapaDesde"),
        Mecanico = TextoOpcional(item, "Mecanico"),
        Servicos = item.TryGetValue("Servicos", out var l) && l.L is { } lista
            ? lista.Select(s => new ServicoPrevisto(Texto(s.M, "Descricao"), Decimal(s.M, "Quantidade"))).ToList()
            : [],
        ObservacoesDiagnostico = TextoOpcional(item, "ObservacoesDiagnostico"),
        MotivoCancelamento = TextoOpcional(item, "MotivoCancelamento"),
        CriadaEm = DataObrigatoria(item, "CriadaEm"),
        DiagnosticoIniciadoEm = DataOpcional(item, "DiagnosticoIniciadoEm"),
        DiagnosticoConcluidoEm = DataOpcional(item, "DiagnosticoConcluidoEm"),
        ExecucaoIniciadaEm = DataOpcional(item, "ExecucaoIniciadaEm"),
        ReparoIniciadoEm = DataOpcional(item, "ReparoIniciadoEm"),
        FinalizadaEm = DataOpcional(item, "FinalizadaEm"),
        CanceladaEm = DataOpcional(item, "CanceladaEm"),
        Version = Inteiro(item, "Version")
    };

    // ───────────── Apontamento (EVT#) ─────────────

    internal static Dictionary<string, AttributeValue> ItemApontamento(int osId, Apontamento a) => new()
    {
        [PK] = S(ChaveExecucao(osId)),
        [SK] = S(ChaveApontamento(a)),
        ["Tipo"] = S("Apontamento"),
        ["Id"] = S(a.Id),
        ["TipoApontamento"] = S(a.Tipo.ToString()),
        ["Texto"] = S(a.Texto),
        ["Autor"] = S(a.Autor),
        ["Em"] = S(Data(a.Em))
    };

    internal static Apontamento ParaApontamento(Dictionary<string, AttributeValue> item) => new(
        Texto(item, "Id"),
        Enum.Parse<TipoApontamento>(Texto(item, "TipoApontamento")),
        Texto(item, "Texto"),
        Texto(item, "Autor"),
        DataObrigatoria(item, "Em"));

    // ───────────── Mensagem processada (MSG#) ─────────────

    internal static Dictionary<string, AttributeValue> ItemMensagemProcessada(Guid messageId, DateTime agora) => new()
    {
        [PK] = S(ChaveMensagem(messageId)),
        [SK] = S(TabelaExecucoes.Mensagem),
        ["Tipo"] = S("MensagemProcessada"),
        ["ProcessadaEm"] = S(Data(agora)),
        // TTL do DynamoDB: segundos desde a época Unix.
        [AtributoTtl] = N(new DateTimeOffset(Utc(agora).Add(RetencaoMensagemProcessada)).ToUnixTimeSeconds())
    };

    internal static Dictionary<string, AttributeValue> ChaveMensagemProcessada(Guid messageId) => new()
    {
        [PK] = S(ChaveMensagem(messageId)),
        [SK] = S(TabelaExecucoes.Mensagem)
    };

    // ───────────── Outbox (OUTBOX#) ─────────────

    internal static Dictionary<string, AttributeValue> ItemOutbox(Guid id, object mensagem, DateTime agora)
    {
        var tipo = mensagem.GetType();
        return new Dictionary<string, AttributeValue>
        {
            [PK] = S(ChaveOutbox(id)),
            [SK] = S(TabelaExecucoes.Outbox),
            ["Tipo"] = S("Outbox"),
            ["Id"] = S(id.ToString()),
            ["TipoMensagem"] = S(tipo.FullName ?? tipo.Name),
            ["Corpo"] = S(JsonSerializer.Serialize(mensagem, tipo, JsonOutbox)),
            ["CriadoEm"] = S(Data(agora)),
            [Gsi2Pk] = S(TabelaExecucoes.Outbox),
            [Gsi2Sk] = S($"{Data(agora)}#{id:N}")
        };
    }

    internal static RegistroOutbox ParaRegistroOutbox(Dictionary<string, AttributeValue> item) => new(
        Guid.Parse(Texto(item, "Id")),
        Texto(item, "TipoMensagem"),
        Texto(item, "Corpo"),
        DataObrigatoria(item, "CriadoEm"));

    internal static Dictionary<string, AttributeValue> ChaveRegistroOutbox(Guid id) => new()
    {
        [PK] = S(ChaveOutbox(id)),
        [SK] = S(TabelaExecucoes.Outbox)
    };

    // ───────────── Auxiliares ─────────────

    internal static AttributeValue S(string valor) => new() { S = valor };
    internal static AttributeValue N(long valor) => new() { N = valor.ToString(CultureInfo.InvariantCulture) };
    internal static AttributeValue N(decimal valor) => new() { N = valor.ToString(CultureInfo.InvariantCulture) };

    private static void Opcional(Dictionary<string, AttributeValue> item, string nome, string? valor)
    {
        if (valor is not null)
            item[nome] = S(valor);
    }

    private static void Opcional(Dictionary<string, AttributeValue> item, string nome, DateTime? valor)
    {
        if (valor is { } data)
            item[nome] = S(Data(data));
    }

    private static string Texto(Dictionary<string, AttributeValue> item, string nome) =>
        TextoOpcional(item, nome) ?? throw new InvalidOperationException($"Item sem o atributo {nome}.");

    private static string? TextoOpcional(Dictionary<string, AttributeValue> item, string nome) =>
        item.TryGetValue(nome, out var v) ? v.S : null;

    private static int Inteiro(Dictionary<string, AttributeValue> item, string nome) =>
        item.TryGetValue(nome, out var v) && v.N is { } n
            ? int.Parse(n, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Item sem o atributo {nome}.");

    private static decimal Decimal(Dictionary<string, AttributeValue> item, string nome) =>
        item.TryGetValue(nome, out var v) && v.N is { } n
            ? decimal.Parse(n, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Item sem o atributo {nome}.");

    private static DateTime DataObrigatoria(Dictionary<string, AttributeValue> item, string nome) =>
        DataOpcional(item, nome) ?? throw new InvalidOperationException($"Item sem o atributo {nome}.");

    private static DateTime? DataOpcional(Dictionary<string, AttributeValue> item, string nome) =>
        TextoOpcional(item, nome) is { } texto
            ? DateTime.Parse(texto, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
}

/// <summary>Mensagem esperando publicação no outbox.</summary>
internal sealed record RegistroOutbox(Guid Id, string TipoMensagem, string Corpo, DateTime CriadoEm);
