using Oficina.Contracts;
using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Application.UseCases;
using Oficina.Execution.Domain;
using Oficina.Execution.UnitTests.TestSupport;

namespace Oficina.Execution.UnitTests.UseCases;

public class ExecucaoUseCasesTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly RepositorioEmMemoria _memoria = new();
    private readonly Relogio _relogio = new(T0);

    private ExecucaoUseCases UseCases(IExecucaoRepositorio? repositorio = null) => new(repositorio ?? _memoria, _relogio);

    private Execucao Semear(int osId = 42)
    {
        var execucao = Execucao.Abrir(osId, Guid.NewGuid(), $"OS-{osId:D4}", new Veiculo("ABC1D23", "VW", "Gol", 2020), null, T0);
        _memoria.Semear(execucao);
        return execucao;
    }

    [Fact]
    public async Task Iniciar_diagnostico_grava_e_publica_DiagnosticoIniciado_com_o_mecanico()
    {
        var semeada = Semear();

        var resultado = await UseCases().IniciarDiagnosticoAsync(42, "joao");

        Assert.Equal(StatusResultado.Ok, resultado.Status);
        Assert.Equal("EmDiagnostico", resultado.Valor!.Etapa);
        var evento = Assert.IsType<DiagnosticoIniciado>(Assert.Single(_memoria.Outbox));
        Assert.Equal("joao", evento.Mecanico);
        Assert.Equal(semeada.CorrelationId, evento.CorrelationId);
        Assert.Equal(T0, evento.OcorridoEm);
    }

    [Fact]
    public async Task Finalizar_publica_a_duracao_do_reparo()
    {
        var execucao = Semear();
        execucao.IniciarDiagnostico("joao", T0);
        execucao.ConcluirDiagnostico(null, "joao", T0);
        execucao.IniciarExecucao([], T0);
        await _memoria.SalvarAsync(execucao, [], null);
        await UseCases().IniciarReparoAsync(42, "joao");

        _relogio.Avancar(TimeSpan.FromMinutes(90));
        var resultado = await UseCases().FinalizarAsync(42, "joao");

        Assert.Equal(StatusResultado.Ok, resultado.Status);
        Assert.Equal(5400, Assert.IsType<ExecucaoFinalizada>(Assert.Single(_memoria.Outbox)).DuracaoSegundos);
    }

    [Fact]
    public async Task Conflito_de_versao_rele_e_aplica_de_novo()
    {
        Semear();
        var repositorio = new ComConflitos(_memoria, conflitos: 2);

        var resultado = await UseCases(repositorio).IniciarDiagnosticoAsync(42, "joao");

        Assert.Equal(StatusResultado.Ok, resultado.Status);
        Assert.Equal(3, repositorio.Tentativas);
        Assert.Single(_memoria.Outbox);
    }

    [Fact]
    public async Task Conflito_que_nao_passa_responde_conflito_sem_gravar()
    {
        Semear();
        var repositorio = new ComConflitos(_memoria, conflitos: 10);

        var resultado = await UseCases(repositorio).IniciarDiagnosticoAsync(42, "joao");

        Assert.Equal(StatusResultado.Conflito, resultado.Status);
        Assert.Contains("ao mesmo tempo", resultado.Erro, StringComparison.Ordinal);
        Assert.Equal(3, repositorio.Tentativas);
        Assert.Empty(_memoria.Outbox);
    }

    [Fact]
    public async Task Dado_invalido_responde_invalido()
    {
        Semear();

        var resultado = await UseCases().RegistrarApontamentoAsync(42, "   ", "joao");

        Assert.Equal(StatusResultado.Invalido, resultado.Status);
        Assert.Null(resultado.Valor);
    }

    [Fact]
    public async Task Etapa_errada_responde_conflito_com_o_motivo()
    {
        Semear();

        var resultado = await UseCases().ConcluirDiagnosticoAsync(42, null, "joao");

        Assert.Equal(StatusResultado.Conflito, resultado.Status);
        Assert.Contains("EmDiagnostico", resultado.Erro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execucao_inexistente_responde_nao_encontrado()
    {
        Assert.Equal(StatusResultado.NaoEncontrado, (await UseCases().IniciarReparoAsync(1, "joao")).Status);
        Assert.Equal(StatusResultado.NaoEncontrado, (await UseCases().RegistrarApontamentoAsync(1, "x", "joao")).Status);
        Assert.Null(await UseCases().ObterAsync(1));
    }

    /// <summary>Simula outra gravação vencendo a corrida nas primeiras tentativas.</summary>
    private sealed class ComConflitos : IExecucaoRepositorio
    {
        private readonly IExecucaoRepositorio _interno;
        private int _conflitosRestantes;

        public ComConflitos(IExecucaoRepositorio interno, int conflitos)
        {
            _interno = interno;
            _conflitosRestantes = conflitos;
        }

        public int Tentativas { get; private set; }

        public Task<Execucao?> ObterAsync(int osId, CancellationToken cancellationToken = default) => _interno.ObterAsync(osId, cancellationToken);

        public Task<IReadOnlyList<EstadoExecucao>> ListarFilaAsync(EtapaExecucao etapa, int limite, CancellationToken cancellationToken = default) =>
            _interno.ListarFilaAsync(etapa, limite, cancellationToken);

        public Task<bool> MensagemJaProcessadaAsync(Guid messageId, CancellationToken cancellationToken = default) =>
            _interno.MensagemJaProcessadaAsync(messageId, cancellationToken);

        public Task<ResultadoGravacao> SalvarAsync(Execucao? execucao, IReadOnlyList<object> mensagens, Guid? mensagemProcessada, CancellationToken cancellationToken = default)
        {
            Tentativas++;
            if (_conflitosRestantes-- > 0)
                throw new ConflitoDeVersaoException("outra gravação venceu");
            return _interno.SalvarAsync(execucao, mensagens, mensagemProcessada, cancellationToken);
        }
    }
}
