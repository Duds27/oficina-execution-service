using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Oficina.Execution.UnitTests.TestSupport;

/// <summary>Coleta, em memória, as medições de um Meter (MeterListener).</summary>
public sealed class ColetorDeMetricas : IDisposable
{
    public sealed record Medicao(string Instrumento, double Valor, IReadOnlyDictionary<string, string?> Tags);

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Medicao> _medicoes = new();

    public ColetorDeMetricas(string nomeMeter)
    {
        _listener.InstrumentPublished = (instrumento, listener) =>
        {
            if (instrumento.Meter.Name == nomeMeter)
                listener.EnableMeasurementEvents(instrumento);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Guardar(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Guardar(i, v, tags));
        _listener.Start();
    }

    public IReadOnlyList<Medicao> Medicoes(string instrumento) =>
        _medicoes.Where(m => m.Instrumento == instrumento).ToList();

    public double Soma(string instrumento) => Medicoes(instrumento).Sum(m => m.Valor);

    /// <summary>Lê os instrumentos observáveis (gauges) agora e devolve o último valor do pedido.</summary>
    public double Observar(string instrumento)
    {
        _listener.RecordObservableInstruments();
        return Medicoes(instrumento)[^1].Valor;
    }

    private void Guardar(Instrument instrumento, double valor, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dicionario = new Dictionary<string, string?>();
        foreach (var tag in tags)
            dicionario[tag.Key] = tag.Value?.ToString();
        _medicoes.Enqueue(new Medicao(instrumento.Name, valor, dicionario));
    }

    public void Dispose() => _listener.Dispose();
}
