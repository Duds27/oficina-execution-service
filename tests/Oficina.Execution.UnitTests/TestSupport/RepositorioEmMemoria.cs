using Oficina.Execution.Application.Abstractions;
using Oficina.Execution.Domain;

namespace Oficina.Execution.UnitTests.TestSupport;

/// <summary>
/// Repositório em memória com as mesmas garantias do DynamoDB: gravação atômica,
/// dedup pelo MessageId e concorrência otimista pela versão.
/// </summary>
public sealed class RepositorioEmMemoria : IExecucaoRepositorio
{
    private readonly object _trava = new();
    private readonly Dictionary<int, (EstadoExecucao Estado, List<Apontamento> Apontamentos)> _execucoes = [];
    private readonly HashSet<Guid> _processadas = [];
    private readonly List<object> _outbox = [];

    public IReadOnlyList<object> Outbox
    {
        get { lock (_trava) return [.. _outbox]; }
    }

    public int Gravacoes { get; private set; }

    /// <summary>Consultas de dedup: uma por tentativa de processar uma mensagem.</summary>
    public int Leituras { get; private set; }

    public Task<Execucao?> ObterAsync(int osId, CancellationToken cancellationToken = default)
    {
        lock (_trava)
        {
            return Task.FromResult(_execucoes.TryGetValue(osId, out var e)
                ? Execucao.Restaurar(e.Estado, [.. e.Apontamentos])
                : null);
        }
    }

    public Task<IReadOnlyList<EstadoExecucao>> ListarFilaAsync(EtapaExecucao etapa, int limite, CancellationToken cancellationToken = default)
    {
        lock (_trava)
        {
            IReadOnlyList<EstadoExecucao> fila = _execucoes.Values
                .Select(e => e.Estado)
                .Where(e => e.Etapa == etapa)
                .OrderBy(e => e.EtapaDesde).ThenBy(e => e.OsId)
                .Take(limite)
                .ToList();
            return Task.FromResult(fila);
        }
    }

    public Task<bool> MensagemJaProcessadaAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        lock (_trava)
        {
            Leituras++;
            return Task.FromResult(_processadas.Contains(messageId));
        }
    }

    public Task<ResultadoGravacao> SalvarAsync(Execucao? execucao, IReadOnlyList<object> mensagens, Guid? mensagemProcessada, CancellationToken cancellationToken = default)
    {
        lock (_trava)
        {
            if (mensagemProcessada is { } id && _processadas.Contains(id))
                return Task.FromResult(ResultadoGravacao.MensagemDuplicada);

            if (execucao is not null)
            {
                var versaoGravada = _execucoes.TryGetValue(execucao.OsId, out var atual) ? atual.Estado.Version : 0;
                var existe = _execucoes.ContainsKey(execucao.OsId);
                if ((execucao.Version == 0 && existe) || (execucao.Version != 0 && versaoGravada != execucao.Version))
                    throw new ConflitoDeVersaoException($"Versão {execucao.Version} desatualizada.");

                var apontamentos = existe ? [.. atual.Apontamentos] : new List<Apontamento>();
                apontamentos.AddRange(execucao.NovosApontamentos);
                _execucoes[execucao.OsId] = (execucao.Estado with { Version = execucao.Version + 1 }, apontamentos);
            }

            if (mensagemProcessada is { } novo)
                _processadas.Add(novo);
            _outbox.AddRange(mensagens);
            Gravacoes++;
        }

        execucao?.MarcarComoGravada();
        return Task.FromResult(ResultadoGravacao.Gravada);
    }

    /// <summary>Atalho dos testes: grava direto uma execução pronta.</summary>
    public void Semear(Execucao execucao)
    {
        lock (_trava)
            _execucoes[execucao.OsId] = (execucao.Estado with { Version = execucao.Version + 1 }, [.. execucao.Apontamentos]);
        execucao.MarcarComoGravada();
    }
}
