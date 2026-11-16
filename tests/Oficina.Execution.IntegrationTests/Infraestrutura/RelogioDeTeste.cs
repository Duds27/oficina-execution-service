namespace Oficina.Execution.IntegrationTests.Infraestrutura;

/// <summary>Relógio controlado pelo teste.</summary>
public sealed class RelogioDeTeste : TimeProvider
{
    private DateTimeOffset _agora;

    public RelogioDeTeste(DateTime inicio) => _agora = new DateTimeOffset(inicio, TimeSpan.Zero);

    public DateTime Agora => _agora.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _agora;

    public DateTime Avancar(TimeSpan quanto)
    {
        _agora = _agora.Add(quanto);
        return Agora;
    }
}
