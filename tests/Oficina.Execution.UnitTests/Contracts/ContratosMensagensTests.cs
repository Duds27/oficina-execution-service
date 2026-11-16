using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MassTransit;
using MassTransit.Serialization;
using Oficina.Contracts;

namespace Oficina.Execution.UnitTests.Contracts;

/// <summary>
/// Testes de contrato (docs/contratos-mensagens.md, seção 6). Quebrar um destes testes
/// significa quebrar o os-service sem perceber.
/// </summary>
public partial class ContratosMensagensTests
{
    private static readonly JsonSerializerOptions Json = SystemTextJsonMessageSerializer.Options;

    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");
    private static readonly DateTime Ocorrido = new(2026, 11, 15, 14, 3, 27, 512, DateTimeKind.Utc);

    /// <summary>Campos de cada mensagem além do envelope, como no catálogo (seção 2).</summary>
    public static TheoryData<Type, string[]> Catalogo => new()
    {
        { typeof(SolicitarDiagnostico), ["Veiculo", "Descricao"] },
        { typeof(DiagnosticoIniciado), ["Mecanico"] },
        { typeof(DiagnosticoConcluido), ["Observacoes"] },
        { typeof(IniciarExecucao), ["Servicos"] },
        { typeof(ExecucaoIniciada), [] },
        { typeof(ExecucaoRejeitada), ["Motivo"] },
        { typeof(ExecucaoFinalizada), ["DuracaoSegundos"] },
        { typeof(CancelarExecucao), ["Motivo"] }
    };

    /// <summary>Um exemplo de cada mensagem, comparado com o JSON gravado em Contracts/&lt;mensagem&gt;.json.</summary>
    public static TheoryData<string, object> Exemplos => new()
    {
        { "SolicitarDiagnostico", Envelope(new SolicitarDiagnostico
            {
                Veiculo = new VeiculoInfo { Placa = "ABC1D23", Marca = "Volkswagen", Modelo = "Gol", Ano = 2020 },
                Descricao = "Barulho na suspensão"
            }) },
        { "IniciarExecucao", Envelope(new IniciarExecucao
            {
                Servicos = [new ServicoExecucaoInfo { Descricao = "Troca de óleo", Quantidade = 1 }, new ServicoExecucaoInfo { Descricao = "Alinhamento", Quantidade = 1.5m }]
            }) },
        { "CancelarExecucao", Envelope(new CancelarExecucao { Motivo = "Orçamento recusado pelo cliente" }) },
        { "DiagnosticoIniciado", Envelope(new DiagnosticoIniciado { Mecanico = "joao.silva" }) },
        { "DiagnosticoConcluido", Envelope(new DiagnosticoConcluido { Observacoes = "Amortecedor dianteiro com vazamento" }) },
        { "ExecucaoIniciada", Envelope(new ExecucaoIniciada()) },
        { "ExecucaoRejeitada", Envelope(new ExecucaoRejeitada { Motivo = "falha simulada" }) },
        { "ExecucaoFinalizada", Envelope(new ExecucaoFinalizada { DuracaoSegundos = 5400 }) }
    };

    private static readonly string[] CamposEnvelope = ["CorrelationId", "OsId", "NumeroOS", "OcorridoEm"];

    [Fact]
    public void Catalogo_tem_as_8_mensagens_do_execution_service()
    {
        var mensagens = typeof(MensagemOS).Assembly.GetTypes()
            .Where(t => t.Namespace == "Oficina.Contracts" && !t.IsAbstract && typeof(MensagemOS).IsAssignableFrom(t))
            .ToHashSet();

        var catalogo = Catalogo.Select(linha => (Type)linha[0]).ToHashSet();

        Assert.Equal(8, mensagens.Count);
        Assert.True(catalogo.SetEquals(mensagens), "Mensagem nova ou removida: atualize o catálogo e docs/contratos-mensagens.md.");
    }

    [Theory]
    [MemberData(nameof(Catalogo))]
    public void Mensagem_tem_URN_e_exchange_derivados_do_nome(Type tipo, string[] _)
    {
        var kebab = Kebab(tipo.Name);

        var urn = tipo.GetCustomAttribute<MessageUrnAttribute>();
        var entity = tipo.GetCustomAttribute<EntityNameAttribute>();

        Assert.NotNull(urn);
        Assert.NotNull(entity);
        Assert.Equal($"urn:message:oficina:{kebab}:v1", urn!.Urn.ToString());
        Assert.Equal($"oficina.{kebab}.v1", entity!.EntityName);
    }

    [Theory]
    [MemberData(nameof(Catalogo))]
    public void Mensagem_tem_exatamente_os_campos_do_catalogo(Type tipo, string[] campos)
    {
        var propriedades = tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .Order()
            .ToArray();

        Assert.Equal(CamposEnvelope.Concat(campos).Order().ToArray(), propriedades);
    }

    [Fact]
    public void Envelope_nao_vira_exchange()
    {
        Assert.NotNull(typeof(MensagemOS).GetCustomAttribute<ExcludeFromTopologyAttribute>());
        Assert.NotNull(typeof(IMensagemOS).GetCustomAttribute<ExcludeFromTopologyAttribute>());
    }

    [Theory]
    [MemberData(nameof(Exemplos))]
    public void Serializacao_bate_com_o_snapshot(string nome, object exemplo)
    {
        var esperado = JsonNode.Parse(Snapshot(nome));
        var atual = JsonSerializer.SerializeToNode(exemplo, exemplo.GetType(), Json);

        Assert.True(JsonNode.DeepEquals(esperado, atual),
            $"{nome} mudou no fio. Atual:{Environment.NewLine}{atual!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    [Theory]
    [MemberData(nameof(Exemplos))]
    public void Snapshot_faz_ida_e_volta_sem_perder_campo(string nome, object exemplo)
    {
        var lido = JsonSerializer.Deserialize(Snapshot(nome), exemplo.GetType(), Json)!;

        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(exemplo, exemplo.GetType(), Json),
            JsonSerializer.SerializeToNode(lido, exemplo.GetType(), Json)));
    }

    [Fact]
    public void SolicitarDiagnostico_do_os_service_desserializa_todos_os_campos()
    {
        var msg = JsonSerializer.Deserialize<SolicitarDiagnostico>(Snapshot("SolicitarDiagnostico"), Json)!;

        Assert.Equal(Correlacao, msg.CorrelationId);
        Assert.Equal(42, msg.OsId);
        Assert.Equal("OS-20261115-0042", msg.NumeroOS);
        Assert.Equal(Ocorrido, msg.OcorridoEm.ToUniversalTime());
        Assert.Equal(new VeiculoInfo { Placa = "ABC1D23", Marca = "Volkswagen", Modelo = "Gol", Ano = 2020 }, msg.Veiculo);
        Assert.Equal("Barulho na suspensão", msg.Descricao);
    }

    [Fact]
    public void IniciarExecucao_sem_servicos_vira_lista_vazia_e_nunca_nula()
    {
        var msg = JsonSerializer.Deserialize<IniciarExecucao>("""{"osId":1,"numeroOS":"OS-1","servicos":[]}""", Json)!;
        var json = JsonSerializer.SerializeToNode(new IniciarExecucao { OsId = 1 }, Json)!;

        Assert.Empty(msg.Servicos);
        Assert.Empty(json["servicos"]!.AsArray());
    }

    [Fact]
    public void Decimal_vai_como_texto_no_fio_e_e_lido_como_texto_ou_numero()
    {
        // O serializador do MassTransit escreve decimal como string, para não perder precisão.
        // Quem lê aceita os dois formatos (o exemplo da seção 2.2 dos contratos usa número).
        var comoTexto = JsonSerializer.Deserialize<IniciarExecucao>("""{"servicos":[{"descricao":"a","quantidade":"1.5"}]}""", Json)!;
        var comoNumero = JsonSerializer.Deserialize<IniciarExecucao>("""{"servicos":[{"descricao":"a","quantidade":1.5}]}""", Json)!;

        Assert.Equal(1.5m, Assert.Single(comoTexto.Servicos).Quantidade);
        Assert.Equal(1.5m, Assert.Single(comoNumero.Servicos).Quantidade);
    }

    private static T Envelope<T>(T mensagem) where T : MensagemOS => mensagem with
    {
        CorrelationId = Correlacao,
        OsId = 42,
        NumeroOS = "OS-20261115-0042",
        OcorridoEm = Ocorrido
    };

    private static string Snapshot(string nome) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", $"{nome}.json"));

    private static string Kebab(string nome) => MaiusculaInterna().Replace(nome, "-$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex MaiusculaInterna();
}
