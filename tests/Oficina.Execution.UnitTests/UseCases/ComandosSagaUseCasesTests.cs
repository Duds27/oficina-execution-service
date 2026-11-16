using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using Oficina.Execution.Application;
using Oficina.Execution.Application.UseCases;
using Oficina.Execution.Domain;
using Oficina.Execution.UnitTests.TestSupport;

namespace Oficina.Execution.UnitTests.UseCases;

public class ComandosSagaUseCasesTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Correlacao = Guid.Parse("3f2c9a4e-8d1b-4c55-9a7e-2b6f0d9e1a11");

    private readonly RepositorioEmMemoria _repositorio = new();
    private readonly Relogio _relogio = new(T0);
    private readonly FaultInjectionOptions _falhas = new();
    private readonly ComandosSagaUseCases _useCases;

    public ComandosSagaUseCasesTests()
    {
        _useCases = new ComandosSagaUseCases(
            _repositorio, _relogio, new MonitorFixo(_falhas), NullLogger<ComandosSagaUseCases>.Instance);
    }

    private static SolicitarDiagnostico Solicitar(int osId = 42) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-20261116-{osId:D4}",
        OcorridoEm = T0,
        Veiculo = new VeiculoInfo { Placa = "ABC1D23", Marca = "VW", Modelo = "Gol", Ano = 2020 },
        Descricao = "Barulho no motor"
    };

    private static IniciarExecucao Iniciar(int osId = 42) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-20261116-{osId:D4}",
        OcorridoEm = T0,
        Servicos = [new ServicoExecucaoInfo { Descricao = "Troca de óleo", Quantidade = 1 }]
    };

    private static CancelarExecucao Cancelar(int osId = 42) => new()
    {
        CorrelationId = Correlacao,
        OsId = osId,
        NumeroOS = $"OS-20261116-{osId:D4}",
        OcorridoEm = T0,
        Motivo = "Orçamento recusado"
    };

    private async Task<Execucao> ComDiagnosticoConcluidoAsync()
    {
        await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());
        var execucao = (await _repositorio.ObterAsync(42))!;
        execucao.IniciarDiagnostico("joao", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        execucao.ConcluirDiagnostico(null, "joao", _relogio.Avancar(TimeSpan.FromMinutes(1)));
        await _repositorio.SalvarAsync(execucao, [], null);
        return execucao;
    }

    [Fact]
    public async Task SolicitarDiagnostico_abre_a_execucao_com_os_dados_da_OS()
    {
        var resultado = await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());

        Assert.Equal(ResultadoProcessamento.Processada, resultado);
        var execucao = (await _repositorio.ObterAsync(42))!;
        Assert.Equal(EtapaExecucao.DiagnosticoPendente, execucao.Etapa);
        Assert.Equal(Correlacao, execucao.CorrelationId);
        Assert.Equal(new Veiculo("ABC1D23", "VW", "Gol", 2020), execucao.Estado.Veiculo);
        Assert.Equal("Barulho no motor", execucao.Estado.Descricao);
        Assert.Empty(_repositorio.Outbox);
    }

    [Fact]
    public async Task Mesma_mensagem_duas_vezes_tem_efeito_uma_vez_so()
    {
        var messageId = Guid.NewGuid();

        await _useCases.SolicitarDiagnosticoAsync(Solicitar(), messageId);
        var repetida = await _useCases.SolicitarDiagnosticoAsync(Solicitar(), messageId);

        Assert.Equal(ResultadoProcessamento.Duplicada, repetida);
        Assert.Equal(1, _repositorio.Gravacoes);
    }

    [Fact]
    public async Task Pedido_de_diagnostico_para_OS_que_ja_tem_execucao_e_ignorado()
    {
        await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());

        var outroPedido = Guid.NewGuid();
        var resultado = await _useCases.SolicitarDiagnosticoAsync(Solicitar(), outroPedido);

        Assert.Equal(ResultadoProcessamento.Ignorada, resultado);
        Assert.Equal(1, (await _repositorio.ObterAsync(42))!.Version);
        Assert.True(await _repositorio.MensagemJaProcessadaAsync(outroPedido));
    }

    [Fact]
    public async Task IniciarExecucao_com_diagnostico_concluido_libera_o_reparo_e_responde_ExecucaoIniciada()
    {
        await ComDiagnosticoConcluidoAsync();

        await _useCases.IniciarExecucaoAsync(Iniciar(), Guid.NewGuid());

        var execucao = (await _repositorio.ObterAsync(42))!;
        Assert.Equal(EtapaExecucao.AguardandoExecucao, execucao.Etapa);
        Assert.Equal([new ServicoPrevisto("Troca de óleo", 1)], execucao.Estado.Servicos);
        var evento = Assert.IsType<ExecucaoIniciada>(Assert.Single(_repositorio.Outbox));
        Assert.Equal(Correlacao, evento.CorrelationId);
        Assert.Equal(42, evento.OsId);
        Assert.Equal("OS-20261116-0042", evento.NumeroOS);
        Assert.Equal(_relogio.Agora, evento.OcorridoEm);
    }

    [Fact]
    public async Task IniciarExecucao_antes_do_diagnostico_concluido_responde_ExecucaoRejeitada()
    {
        await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());

        await _useCases.IniciarExecucaoAsync(Iniciar(), Guid.NewGuid());

        var evento = Assert.IsType<ExecucaoRejeitada>(Assert.Single(_repositorio.Outbox));
        Assert.Contains("DiagnosticoPendente", evento.Motivo, StringComparison.Ordinal);
        Assert.Equal(EtapaExecucao.DiagnosticoPendente, (await _repositorio.ObterAsync(42))!.Etapa);
    }

    [Fact]
    public async Task IniciarExecucao_sem_execucao_responde_ExecucaoRejeitada_com_o_envelope_do_comando()
    {
        await _useCases.IniciarExecucaoAsync(Iniciar(7), Guid.NewGuid());

        var evento = Assert.IsType<ExecucaoRejeitada>(Assert.Single(_repositorio.Outbox));
        Assert.Equal(7, evento.OsId);
        Assert.Equal(Correlacao, evento.CorrelationId);
        Assert.Contains("Não há execução", evento.Motivo, StringComparison.Ordinal);
        Assert.Null(await _repositorio.ObterAsync(7));
    }

    [Fact]
    public async Task Com_fault_injection_todo_IniciarExecucao_e_rejeitado_com_falha_simulada()
    {
        await ComDiagnosticoConcluidoAsync();
        _falhas.RejeitarExecucao = true;

        await _useCases.IniciarExecucaoAsync(Iniciar(), Guid.NewGuid());

        var evento = Assert.IsType<ExecucaoRejeitada>(Assert.Single(_repositorio.Outbox));
        Assert.Equal("falha simulada", evento.Motivo);
        var execucao = (await _repositorio.ObterAsync(42))!;
        Assert.Equal(EtapaExecucao.Cancelada, execucao.Etapa);
        Assert.Equal("falha simulada", execucao.Estado.MotivoCancelamento);
    }

    [Fact]
    public async Task IniciarExecucao_repetido_publica_uma_resposta_so()
    {
        await ComDiagnosticoConcluidoAsync();
        var messageId = Guid.NewGuid();

        await _useCases.IniciarExecucaoAsync(Iniciar(), messageId);
        var repetida = await _useCases.IniciarExecucaoAsync(Iniciar(), messageId);

        Assert.Equal(ResultadoProcessamento.Duplicada, repetida);
        Assert.IsType<ExecucaoIniciada>(Assert.Single(_repositorio.Outbox));
    }

    [Fact]
    public async Task CancelarExecucao_cancela_a_execucao_em_andamento()
    {
        await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());

        var resultado = await _useCases.CancelarExecucaoAsync(Cancelar(), Guid.NewGuid());

        Assert.Equal(ResultadoProcessamento.Processada, resultado);
        var execucao = (await _repositorio.ObterAsync(42))!;
        Assert.Equal(EtapaExecucao.Cancelada, execucao.Etapa);
        Assert.Equal("Orçamento recusado", execucao.Estado.MotivoCancelamento);
        Assert.Empty(_repositorio.Outbox);
    }

    [Fact]
    public async Task CancelarExecucao_em_etapa_final_e_ignorado()
    {
        await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());
        await _useCases.CancelarExecucaoAsync(Cancelar(), Guid.NewGuid());
        var antes = (await _repositorio.ObterAsync(42))!;

        var resultado = await _useCases.CancelarExecucaoAsync(Cancelar() with { Motivo = "de novo" }, Guid.NewGuid());

        Assert.Equal(ResultadoProcessamento.Ignorada, resultado);
        var depois = (await _repositorio.ObterAsync(42))!;
        Assert.Equal(antes.Version, depois.Version);
        Assert.Equal("Orçamento recusado", depois.Estado.MotivoCancelamento);
    }

    [Fact]
    public async Task Cancelamento_antes_do_pedido_de_diagnostico_impede_abrir_execucao_viva()
    {
        var cancelamento = await _useCases.CancelarExecucaoAsync(Cancelar(), Guid.NewGuid());
        var pedidoAtrasado = await _useCases.SolicitarDiagnosticoAsync(Solicitar(), Guid.NewGuid());

        Assert.Equal(ResultadoProcessamento.Processada, cancelamento);
        Assert.Equal(ResultadoProcessamento.Ignorada, pedidoAtrasado);
        Assert.Equal(EtapaExecucao.Cancelada, (await _repositorio.ObterAsync(42))!.Etapa);
    }

    [Fact]
    public async Task Comando_repetido_de_cancelamento_e_detectado_antes_de_ler_a_execucao()
    {
        var messageId = Guid.NewGuid();
        await _useCases.CancelarExecucaoAsync(Cancelar(), messageId);

        Assert.Equal(ResultadoProcessamento.Duplicada, await _useCases.CancelarExecucaoAsync(Cancelar(), messageId));
        Assert.Equal(1, _repositorio.Gravacoes);
    }

    private sealed class MonitorFixo : IOptionsMonitor<FaultInjectionOptions>
    {
        public MonitorFixo(FaultInjectionOptions valor) => CurrentValue = valor;

        public FaultInjectionOptions CurrentValue { get; }

        public FaultInjectionOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<FaultInjectionOptions, string?> listener) => null;
    }
}
