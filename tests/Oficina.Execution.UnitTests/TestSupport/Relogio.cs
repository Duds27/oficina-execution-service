namespace Oficina.Execution.UnitTests.TestSupport;

/// <summary>Relógio controlado pelo teste.</summary>
public sealed class Relogio : TimeProvider
{
    private DateTimeOffset _agora;

    public Relogio(DateTime inicio) => _agora = new DateTimeOffset(inicio, TimeSpan.Zero);

    public DateTime Agora => _agora.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _agora;

    public DateTime Avancar(TimeSpan quanto)
    {
        _agora = _agora.Add(quanto);
        return Agora;
    }
}
