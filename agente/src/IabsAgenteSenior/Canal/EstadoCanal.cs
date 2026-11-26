namespace IabsAgenteSenior.Canal;

/// <summary>Situação do canal para o painel (ícone e janela). Seguro entre threads.</summary>
public sealed class EstadoCanal
{
    private readonly Lock _trava = new();
    private bool _conectado;
    private DateTimeOffset? _conectadoDesde;
    private DateTimeOffset? _desconectadoDesde;
    private DateTimeOffset? _ultimoPedidoEm;
    private long _pedidos;
    private string? _conexaoIabs;
    private string? _mensagem;

    public void Conectou(string? conexaoIabs)
    {
        lock (_trava)
        {
            _conectado = true;
            _conectadoDesde = DateTimeOffset.Now;
            _desconectadoDesde = null;
            _conexaoIabs = conexaoIabs;
            _mensagem = null;
        }
    }

    public void Desconectou(string mensagem)
    {
        lock (_trava)
        {
            if (_conectado || _desconectadoDesde is null) _desconectadoDesde = DateTimeOffset.Now;
            _conectado = false;
            _conectadoDesde = null;
            _mensagem = mensagem;
        }
    }

    public void Atendeu()
    {
        lock (_trava)
        {
            _ultimoPedidoEm = DateTimeOffset.Now;
            _pedidos++;
        }
    }

    public Retrato Ler()
    {
        lock (_trava)
        {
            return new Retrato(_conectado, _conectadoDesde, _desconectadoDesde, _ultimoPedidoEm, _pedidos, _conexaoIabs, _mensagem);
        }
    }

    public sealed record Retrato(
        bool Conectado,
        DateTimeOffset? ConectadoDesde,
        DateTimeOffset? DesconectadoDesde,
        DateTimeOffset? UltimoPedidoEm,
        long Pedidos,
        string? ConexaoIabs,
        string? Mensagem);
}
