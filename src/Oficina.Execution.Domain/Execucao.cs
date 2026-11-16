namespace Oficina.Execution.Domain;

/// <summary>
/// Estado persistido da execução. O agregado guarda um e o substitui (<c>with</c>) a cada
/// mudança; o repositório grava e restaura este mesmo registro.
/// </summary>
public sealed record EstadoExecucao
{
    public required int OsId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string NumeroOS { get; init; }

    /// <summary>Nulo só quando o cancelamento chegou antes do pedido de diagnóstico.</summary>
    public Veiculo? Veiculo { get; init; }
    public string? Descricao { get; init; }

    public required EtapaExecucao Etapa { get; init; }

    /// <summary>Entrada na etapa atual. Ordena a fila: quem chegou antes, sai antes.</summary>
    public required DateTime EtapaDesde { get; init; }

    public string? Mecanico { get; init; }
    public IReadOnlyList<ServicoPrevisto> Servicos { get; init; } = [];
    public string? ObservacoesDiagnostico { get; init; }
    public string? MotivoCancelamento { get; init; }

    public required DateTime CriadaEm { get; init; }
    public DateTime? DiagnosticoIniciadoEm { get; init; }
    public DateTime? DiagnosticoConcluidoEm { get; init; }
    public DateTime? ExecucaoIniciadaEm { get; init; }
    public DateTime? ReparoIniciadoEm { get; init; }
    public DateTime? FinalizadaEm { get; init; }
    public DateTime? CanceladaEm { get; init; }

    /// <summary>Versão gravada no banco (concorrência otimista). Zero: ainda não foi gravada.</summary>
    public required int Version { get; init; }
}

/// <summary>
/// Execução de uma OS: fila de diagnóstico e reparo, com a linha do tempo de apontamentos.
/// As regras de etapa ficam aqui; avisar o os-service é papel da camada de aplicação.
/// </summary>
public sealed class Execucao
{
    public const string AutorSistema = "sistema";

    private readonly List<Apontamento> _apontamentos;
    private readonly List<Apontamento> _novos = [];

    private Execucao(EstadoExecucao estado, IEnumerable<Apontamento> apontamentos)
    {
        Estado = estado;
        _apontamentos = apontamentos.OrderBy(a => a.Em).ToList();
    }

    public EstadoExecucao Estado { get; private set; }

    public int OsId => Estado.OsId;
    public Guid CorrelationId => Estado.CorrelationId;
    public string NumeroOS => Estado.NumeroOS;
    public EtapaExecucao Etapa => Estado.Etapa;
    public int Version => Estado.Version;
    public bool Encerrada => Etapa is EtapaExecucao.Finalizada or EtapaExecucao.Cancelada;

    /// <summary>Linha do tempo completa, em ordem cronológica.</summary>
    public IReadOnlyList<Apontamento> Apontamentos => _apontamentos;

    /// <summary>Apontamentos criados desde a última gravação.</summary>
    public IReadOnlyList<Apontamento> NovosApontamentos => _novos;

    /// <summary>Entra na fila de diagnóstico (comando <c>SolicitarDiagnostico</c>).</summary>
    public static Execucao Abrir(int osId, Guid correlationId, string numeroOS, Veiculo veiculo, string? descricao, DateTime agora)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(osId);
        ArgumentException.ThrowIfNullOrWhiteSpace(numeroOS);
        ArgumentNullException.ThrowIfNull(veiculo);

        var execucao = new Execucao(NovoEstado(osId, correlationId, numeroOS, agora) with
        {
            Veiculo = veiculo,
            Descricao = descricao
        }, []);
        execucao.Registrar(TipoApontamento.Abertura, $"OS {numeroOS} entrou na fila de diagnóstico.", AutorSistema, agora);
        return execucao;
    }

    /// <summary>
    /// <c>CancelarExecucao</c> chegou antes do <c>SolicitarDiagnostico</c> (seção 5, regra 5 dos contratos).
    /// Registra a execução já cancelada, para um pedido de diagnóstico atrasado não abrir uma execução viva.
    /// </summary>
    public static Execucao AbrirCancelada(int osId, Guid correlationId, string numeroOS, string motivo, DateTime agora)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(osId);
        ArgumentException.ThrowIfNullOrWhiteSpace(numeroOS);

        var execucao = new Execucao(NovoEstado(osId, correlationId, numeroOS, agora), []);
        execucao.Encerrar(motivo, agora);
        return execucao;
    }

    /// <summary>Reconstrói o agregado a partir do que está gravado.</summary>
    public static Execucao Restaurar(EstadoExecucao estado, IEnumerable<Apontamento> apontamentos)
    {
        ArgumentNullException.ThrowIfNull(estado);
        ArgumentNullException.ThrowIfNull(apontamentos);
        return new Execucao(estado, apontamentos);
    }

    public void IniciarDiagnostico(string mecanico, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mecanico);
        ExigirEtapa(EtapaExecucao.DiagnosticoPendente, "iniciar o diagnóstico");

        Estado = Estado with { Mecanico = mecanico, DiagnosticoIniciadoEm = agora };
        MudarEtapa(EtapaExecucao.EmDiagnostico, agora);
        Registrar(TipoApontamento.DiagnosticoIniciado, $"Diagnóstico iniciado por {mecanico}.", mecanico, agora);
    }

    /// <summary>Só conclui o diagnóstico que está em andamento.</summary>
    public void ConcluirDiagnostico(string? observacoes, string autor, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autor);
        ExigirEtapa(EtapaExecucao.EmDiagnostico, "concluir o diagnóstico");

        var texto = string.IsNullOrWhiteSpace(observacoes) ? null : observacoes.Trim();
        Estado = Estado with { ObservacoesDiagnostico = texto, DiagnosticoConcluidoEm = agora };
        MudarEtapa(EtapaExecucao.DiagnosticoConcluido, agora);
        Registrar(TipoApontamento.DiagnosticoConcluido,
            texto is null ? "Diagnóstico concluído." : $"Diagnóstico concluído: {texto}", autor, agora);
    }

    /// <summary>
    /// Pagamento confirmado, a saga libera o reparo (comando <c>IniciarExecucao</c>).
    /// Só é aceito com o diagnóstico concluído; fora disso, a resposta é a rejeição.
    /// </summary>
    public ResultadoInicioExecucao IniciarExecucao(IReadOnlyList<ServicoPrevisto> servicos, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(servicos);

        if (Etapa != EtapaExecucao.DiagnosticoConcluido)
        {
            var motivo = $"A execução da OS {NumeroOS} está em {Etapa}; o reparo só começa com o diagnóstico concluído.";
            RejeitarInicio(motivo, agora);
            return ResultadoInicioExecucao.Rejeitar(motivo);
        }

        Estado = Estado with { Servicos = [.. servicos], ExecucaoIniciadaEm = agora };
        MudarEtapa(EtapaExecucao.AguardandoExecucao, agora);
        Registrar(TipoApontamento.ExecucaoIniciada, "Pagamento confirmado: reparo liberado.", AutorSistema, agora);
        return ResultadoInicioExecucao.Aceitar();
    }

    /// <summary>
    /// Registra a rejeição do início. Uma execução que ainda não começou (diagnóstico concluído)
    /// passa a <see cref="EtapaExecucao.Cancelada"/>: a saga compensa e cancela a OS, sem mandar
    /// <c>CancelarExecucao</c>, e nada mais a tiraria da fila. Nas outras etapas nada muda.
    /// </summary>
    public void RejeitarInicio(string motivo, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);

        Registrar(TipoApontamento.ExecucaoRejeitada, $"Início do reparo rejeitado: {motivo}", AutorSistema, agora);
        if (Etapa == EtapaExecucao.DiagnosticoConcluido)
            Encerrar(motivo, agora);
    }

    public void IniciarReparo(string autor, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autor);
        ExigirEtapa(EtapaExecucao.AguardandoExecucao, "iniciar o reparo");

        Estado = Estado with { ReparoIniciadoEm = agora };
        MudarEtapa(EtapaExecucao.EmReparo, agora);
        Registrar(TipoApontamento.ReparoIniciado, "Reparo iniciado.", autor, agora);
    }

    /// <summary>Observação livre do mecânico, em qualquer etapa não final.</summary>
    public void RegistrarObservacao(string texto, string autor, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);
        ArgumentException.ThrowIfNullOrWhiteSpace(autor);
        if (Encerrada)
            throw new EtapaInvalidaException($"A execução da OS {NumeroOS} está {Etapa} e não aceita novas observações.");

        Registrar(TipoApontamento.Observacao, texto.Trim(), autor, agora);
    }

    /// <summary>Finaliza o reparo e devolve a duração dele, em segundos.</summary>
    public int Finalizar(string autor, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(autor);
        ExigirEtapa(EtapaExecucao.EmReparo, "finalizar o reparo");

        var inicio = Estado.ReparoIniciadoEm ?? agora;
        var duracao = (int)Math.Max(0, Math.Round((agora - inicio).TotalSeconds));
        Estado = Estado with { FinalizadaEm = agora };
        MudarEtapa(EtapaExecucao.Finalizada, agora);
        Registrar(TipoApontamento.Finalizada, $"Reparo finalizado em {duracao} s.", autor, agora);
        return duracao;
    }

    /// <summary>
    /// Compensação da saga (comando <c>CancelarExecucao</c>). Em etapa final não faz nada
    /// e devolve <c>false</c>: o comando pode chegar repetido ou atrasado.
    /// </summary>
    public bool Cancelar(string motivo, DateTime agora)
    {
        if (Encerrada)
            return false;

        Encerrar(motivo, agora);
        return true;
    }

    /// <summary>Chamado pelo repositório depois de gravar: avança a versão e zera os apontamentos novos.</summary>
    public void MarcarComoGravada()
    {
        Estado = Estado with { Version = Estado.Version + 1 };
        _novos.Clear();
    }

    private static EstadoExecucao NovoEstado(int osId, Guid correlationId, string numeroOS, DateTime agora) => new()
    {
        OsId = osId,
        CorrelationId = correlationId,
        NumeroOS = numeroOS,
        Etapa = EtapaExecucao.DiagnosticoPendente,
        EtapaDesde = agora,
        CriadaEm = agora,
        Version = 0
    };

    private void Encerrar(string motivo, DateTime agora)
    {
        var texto = string.IsNullOrWhiteSpace(motivo) ? "sem motivo informado" : motivo.Trim();
        Estado = Estado with { MotivoCancelamento = texto, CanceladaEm = agora };
        MudarEtapa(EtapaExecucao.Cancelada, agora);
        Registrar(TipoApontamento.Cancelada, $"Execução cancelada: {texto}", AutorSistema, agora);
    }

    private void ExigirEtapa(EtapaExecucao esperada, string operacao)
    {
        if (Etapa != esperada)
            throw new EtapaInvalidaException(
                $"Não é possível {operacao} da OS {NumeroOS}: a execução está em {Etapa}, e a operação exige {esperada}.");
    }

    private void MudarEtapa(EtapaExecucao nova, DateTime agora) =>
        Estado = Estado with { Etapa = nova, EtapaDesde = agora };

    private void Registrar(TipoApontamento tipo, string texto, string autor, DateTime agora)
    {
        var apontamento = Apontamento.Novo(tipo, texto, autor, agora);
        _apontamentos.Add(apontamento);
        _novos.Add(apontamento);
    }
}
